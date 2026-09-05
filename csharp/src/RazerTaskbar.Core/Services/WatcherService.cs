// Port of src/watcher.rs (originally watcherV3.ts / watcherV4.ts /
// razer_watcher.ts).
//
// - V3: `%LOCALAPPDATA%/Razer/Synapse3/Log/Razer Synapse 3.log`
// - V4: `%LOCALAPPDATA%/Razer/RazerAppEngine/User Data/Logs/systray_systrayv2*.log`
//   the whole history is replayed; the last snapshot decides connection state.
// - FileSystemWatcher watches the files; a parse runs within 1s of any write
//   event and the poll loop re-parses every `polling_throttle_secs` as a
//   fallback (settings are re-read from disk each pass, so edits apply
//   without restart).

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RazerTaskbar.Core;

/// <summary>Thread-safe device map shared between the watcher and UI threads
/// (the C# stand-in for Rust's `Arc<Mutex<DeviceMap>>`).</summary>
public sealed class DeviceStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, RazerDevice> _map = new();

    public void Mutate(Action<Dictionary<string, RazerDevice>> edit)
    {
        lock (_lock)
        {
            edit(_map);
        }
    }

    public T Read<T>(Func<IReadOnlyDictionary<string, RazerDevice>, T> read)
    {
        lock (_lock)
        {
            return read(_map);
        }
    }

    public Dictionary<string, RazerDevice> Snapshot()
    {
        lock (_lock)
        {
            return new Dictionary<string, RazerDevice>(_map);
        }
    }
}

/// <summary>V4 connection rule: the device must appear in the last
/// `connectingDeviceData` snapshot AND be powered on. Synapse keeps powered-off
/// devices listed with `chargingStatus: "off"`. An empty charging status (null
/// powerStatus on wired replug) counts as off too.</summary>
public static class V4Rules
{
    public static bool IsConnected(HashSet<string> ids, HashSet<string> off, string handle)
        => ids.Contains(handle) && !off.Contains(handle);
}

/// <summary>One entry of a V4 `connectingDeviceData` snapshot.
/// camelCase names are load-bearing; every field tolerates missing AND
/// explicit-null values by falling back to its default (Synapse logs emit
/// explicit nulls; one null field must not abort the whole snapshot).</summary>
public sealed class V4Device
{
    [JsonPropertyName("serialNumber")]
    public string? SerialNumberRaw { get; set; }

    [JsonPropertyName("hasBattery")]
    public bool? HasBatteryRaw { get; set; }

    [JsonPropertyName("deviceContainerId")]
    public string? DeviceContainerIdRaw { get; set; }

    [JsonPropertyName("powerStatus")]
    public V4Power? PowerStatusRaw { get; set; }

    [JsonPropertyName("name")]
    public V4Name? NameRaw { get; set; }

    /// <summary>"MOUSE" / "KEYBOARD" / "HEADSET" / … — drives the device-type icon.</summary>
    [JsonPropertyName("category")]
    public string? CategoryRaw { get; set; }

    /// <summary>Device-side battery saver / low-power mode: 0 = off, nonzero = on.</summary>
    [JsonPropertyName("lowPowerMode")]
    public long? LowPowerModeRaw { get; set; }

    public string SerialNumber => SerialNumberRaw ?? "";
    public bool HasBattery => HasBatteryRaw ?? false;
    public string DeviceContainerId => DeviceContainerIdRaw ?? "";
    public V4Power PowerStatus => PowerStatusRaw ?? new V4Power();
    public V4Name Name => NameRaw ?? new V4Name();
    public string Category => CategoryRaw ?? "";
    public long LowPowerMode => LowPowerModeRaw ?? 0;
}

public sealed class V4Power
{
    [JsonPropertyName("chargingStatus")]
    public string? ChargingStatusRaw { get; set; }

    [JsonPropertyName("level")]
    public int? LevelRaw { get; set; }

    public string ChargingStatus => ChargingStatusRaw ?? "";
    public int Level => LevelRaw ?? 0;
}

public sealed class V4Name
{
    [JsonPropertyName("en")]
    public string? EnRaw { get; set; }

    public string En => EnRaw ?? "";
}

public sealed class RazerWatcher
{
    /// <summary>How soon after the first filesystem event of a burst a parse
    /// runs. Synapse writes whole snapshot batches at once, so 1s coalesces
    /// the burst without adding perceptible latency.</summary>
    private static readonly TimeSpan EventDebounce = TimeSpan.FromSeconds(1);

    private static readonly Regex V4FileRegex = new(@"^systray_systrayv2(?<index>\d*)\.log$", RegexOptions.Compiled);
    private static readonly Regex V4LineRegex = new(@"(?m)^\[(?<timestamp>.+?)\].*connectingDeviceData: (?<json>.+)$", RegexOptions.Compiled);
    private static readonly Regex V3BatteryRegex = new(
        @"(?m)^(?<dateTime>.+?) INFO.+?_OnBatteryLevelChanged[\s\S]*?Name: (?<name>.*)[\s\S]*?Handle: (?<handle>\d+)[\s\S]*?level (?<level>\d+) state (?<isCharging>\d+)",
        RegexOptions.Compiled);
    private static readonly Regex V3LoadedRegex = new(
        @"(?m)^(?<dateTime>.+?) INFO.+?_OnDeviceLoaded[\s\S]*?Name: (?<name>.*)[\s\S]*?Handle: (?<handle>\d+)",
        RegexOptions.Compiled);
    private static readonly Regex V3RemovedRegex = new(
        @"(?m)^(?<dateTime>.+?) INFO.+?_OnDeviceRemoved[\s\S]*?Name: (?<name>.*)[\s\S]*?Handle: (?<handle>\d+)",
        RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOpts = new();

    private readonly DeviceStore _devices;
    private readonly object _lastV4TimestampLock = new();
    private string _lastV4Timestamp = "";

    public RazerWatcher(DeviceStore devices)
    {
        _devices = devices;
    }

    public static string? V3LogPath()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        return string.IsNullOrEmpty(localAppData)
            ? null
            : Path.Combine(localAppData, "Razer", "Synapse3", "Log", "Razer Synapse 3.log");
    }

    public static string? V4LogDir()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        return string.IsNullOrEmpty(localAppData)
            ? null
            : Path.Combine(localAppData, "Razer", "RazerAppEngine", "User Data", "Logs");
    }

    /// <summary>Highest-index `systray_systrayv2*.log` (mirrors findLatestSynapseV4LogFile).</summary>
    public static string? LatestV4Log(string dir)
    {
        (long Idx, string Path)? best = null;
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFiles(dir);
        }
        catch (Exception)
        {
            return null;
        }
        foreach (var path in entries)
        {
            var name = Path.GetFileName(path);
            var m = V4FileRegex.Match(name);
            if (!m.Success)
            {
                continue;
            }
            var idxStr = m.Groups["index"].Value;
            long idx = long.TryParse(idxStr, out var parsed) ? parsed : -1;
            if (best is null || best.Value.Idx < idx)
            {
                best = (idx, path);
            }
        }
        return best?.Path;
    }

    public void Run(int initialPollSeconds)
    {
        // Initial parse so the widget shows something immediately.
        ParseOnce();

        long initialPollMs = initialPollSeconds * 1000L;
        long intervalMs = initialPollMs;
        long lastParse = Environment.TickCount64;
        long? dirtyAt = null;

        using var signal = new SemaphoreSlim(0, int.MaxValue);
        var watchers = new List<FileSystemWatcher>();
        void Watch(FileSystemWatcher w)
        {
            w.Changed += (_, _) => signal.Release();
            w.Created += (_, _) => signal.Release();
            w.Renamed += (_, _) => signal.Release();
            w.Deleted += (_, _) => signal.Release();
            w.Error += (_, _) => signal.Release(); // buffer overflow: just re-parse
            w.EnableRaisingEvents = true;
            watchers.Add(w);
        }

        var v3 = V3LogPath();
        if (v3 is not null && File.Exists(v3))
        {
            var w = new FileSystemWatcher(Path.GetDirectoryName(v3)!, Path.GetFileName(v3));
            Watch(w);
        }
        var v4Dir = V4LogDir();
        if (v4Dir is not null && Directory.Exists(v4Dir))
        {
            var w = new FileSystemWatcher(v4Dir, "systray_systrayv2*.log");
            Watch(w);
        }

        // Parse when either comes due: a filesystem event (coalesced for
        // EventDebounce) or the fallback poll interval. History sampling rides
        // on every parse either way.
        while (true)
        {
            long now = Environment.TickCount64;
            long pollDue = lastParse + intervalMs;
            long due = dirtyAt is { } t ? Math.Min(t + (long)EventDebounce.TotalMilliseconds, pollDue) : pollDue;
            long waitMs = due - now;
            if (waitMs <= 0)
            {
                // Re-resolve the V4 log in case Synapse rotated to a new file.
                ParseOnce();
                var cfg = ConfigService.Load();
                if (cfg.RecordBatteryHistory)
                {
                    HistoryService.Record(_devices);
                }
                // Long-lived loop: re-read the throttle (edits apply live).
                // While recording, the tighter record interval wins.
                long poll = (long)Math.Clamp((long)cfg.PollingThrottleSecs, 0, long.MaxValue >> 1);
                long hist = (long)Math.Clamp((long)cfg.HistoryPollIntervalSecs, 0, long.MaxValue >> 1);
                intervalMs = cfg.RecordBatteryHistory && HistoryService.Ready()
                    ? Math.Max(1, Math.Min(poll, hist))
                    : Math.Max(2, poll);
                lastParse = Environment.TickCount64;
                dirtyAt = null;
                Drain(signal);
                continue;
            }
            if (signal.Wait((int)Math.Min(waitMs, int.MaxValue - 1)))
            {
                Drain(signal);
                dirtyAt ??= Environment.TickCount64;
            }
        }
    }

    private static void Drain(SemaphoreSlim signal)
    {
        while (signal.CurrentCount > 0)
        {
            if (!signal.Wait(0))
            {
                break;
            }
        }
    }

    private void ParseOnce()
    {
        // Re-read settings every pass: a startup copy would clobber edits to
        // the shown device.
        switch (ConfigService.Load().SynapseVersion)
        {
            case "v3": ParseV3(); break;
            case "v4": ParseV4(); break;
            default:
                // auto: V4 wins when its log dir has candidates (mirrors TS `auto`).
                var hasV4 = V4LogDir() is { } d && LatestV4Log(d) is not null;
                if (hasV4)
                {
                    ParseV4();
                }
                else
                {
                    ParseV3();
                }
                break;
        }
    }

    private void ParseV3()
    {
        var path = V3LogPath();
        if (path is null)
        {
            return;
        }
        string log;
        try
        {
            log = File.ReadAllText(path);
        }
        catch (Exception)
        {
            return;
        }

        // Last match per handle wins; connection = loaded after removed (by offset).
        static Dictionary<string, (int Index, Match Match)> LastByHandle(Regex re, string log)
        {
            var map = new Dictionary<string, (int, Match)>();
            foreach (Match m in re.Matches(log))
            {
                map[m.Groups["handle"].Value] = (m.Index, m);
            }
            return map;
        }

        var battery = LastByHandle(V3BatteryRegex, log);
        var loaded = LastByHandle(V3LoadedRegex, log);
        var removed = LastByHandle(V3RemovedRegex, log);

        var shown = ConfigService.Load().ShownDeviceHandle;
        _devices.Mutate(devices =>
        {
            foreach (var (handle, (_, m)) in battery)
            {
                var name = m.Groups["name"].Value;
                int level = Math.Min(int.TryParse(m.Groups["level"].Value, out var l) ? l : 0, 100);
                bool charging = m.Groups["isCharging"].Value != "0";
                // TS parity: missing events count as index -1, so a
                // battery-only device with no load/remove info stays
                // disconnected instead of showing stale state.
                long loadedIdx = loaded.TryGetValue(handle, out var lo) ? lo.Index : -1;
                long removedIdx = removed.TryGetValue(handle, out var re) ? re.Index : -1;
                bool connected = loadedIdx > removedIdx;
                // V3 has no category: classify by product-name keywords.
                devices[handle] = new RazerDevice(
                    name, handle, level, charging,
                    BatterySaver: false,
                    IsConnected: connected,
                    IsSelected: shown.Length == 0 || shown == handle,
                    Kind: DeviceClassifier.FromCategoryAndName("", name));
            }
        });
    }

    private void ParseV4()
    {
        var dir = V4LogDir();
        if (dir is null)
        {
            return;
        }
        var path = LatestV4Log(dir);
        if (path is null)
        {
            return;
        }
        string log;
        try
        {
            log = File.ReadAllText(path);
        }
        catch (Exception)
        {
            return;
        }

        // Every snapshot in the file, oldest first (TS replays the whole
        // history so devices missing from the latest snapshot stay known,
        // marked disconnected).
        var snapshots = new List<(string Ts, string Json)>();
        foreach (Match m in V4LineRegex.Matches(log))
        {
            snapshots.Add((m.Groups["timestamp"].Value, m.Groups["json"].Value));
        }
        if (snapshots.Count == 0)
        {
            return;
        }
        var (lastTs, lastJson) = snapshots[^1];
        lock (_lastV4TimestampLock)
        {
            if (_lastV4Timestamp == lastTs)
            {
                return;
            }
        }

        // A corrupt latest line must NOT advance the timestamp: retry it on
        // the next pass instead of freezing every device as disconnected.
        List<JsonElement> lastVals;
        try
        {
            lastVals = JsonSerializer.Deserialize<List<JsonElement>>(lastJson, JsonOpts) ?? new();
        }
        catch (JsonException)
        {
            return;
        }

        // Connection = membership in the LAST snapshot (serial or container id)
        // AND powered on. A null powerStatus (transitional wired replug
        // snapshot) is treated the same as off.
        var connectedIds = new HashSet<string>();
        var offIds = new HashSet<string>();
        foreach (var v in lastVals)
        {
            if (TryParseDevice(v, out var d))
            {
                bool off = d.PowerStatus.ChargingStatus.Length == 0
                    || d.PowerStatus.ChargingStatus == "off";
                if (d.SerialNumber.Length > 0)
                {
                    connectedIds.Add(d.SerialNumber);
                    if (off)
                    {
                        offIds.Add(d.SerialNumber);
                    }
                }
                if (d.DeviceContainerId.Length > 0)
                {
                    connectedIds.Add(d.DeviceContainerId);
                    if (off)
                    {
                        offIds.Add(d.DeviceContainerId);
                    }
                }
            }
        }

        var shown = ConfigService.Load().ShownDeviceHandle;
        _devices.Mutate(devices =>
        {
            foreach (var (_, json) in snapshots)
            {
                List<JsonElement> vals;
                try
                {
                    vals = JsonSerializer.Deserialize<List<JsonElement>>(json, JsonOpts) ?? new();
                }
                catch (JsonException)
                {
                    continue;
                }
                foreach (var v in vals)
                {
                    if (!TryParseDevice(v, out var d) || !d.HasBattery)
                    {
                        continue;
                    }
                    // TS: `x.serialNumber ?? x.deviceContainerId`; empty
                    // strings fall back too.
                    var handle = d.SerialNumber.Length > 0 ? d.SerialNumber : d.DeviceContainerId;
                    if (handle.Length == 0)
                    {
                        continue;
                    }
                    devices[handle] = new RazerDevice(
                        d.Name.En, handle, Math.Min(d.PowerStatus.Level, 100),
                        d.PowerStatus.ChargingStatus == "Charging",
                        d.LowPowerMode != 0,
                        V4Rules.IsConnected(connectedIds, offIds, handle),
                        shown.Length == 0 || shown == handle,
                        DeviceClassifier.FromCategoryAndName(d.Category, d.Name.En));
                }
            }
            // Drop the NOSERIALNUMBER duplicate once the real serial resolves (TS parity).
            if (devices.TryGetValue("NOSERIALNUMBER", out var noSerial)
                && devices.Values.Any(d => d.Handle != "NOSERIALNUMBER" && d.Name == noSerial.Name))
            {
                devices.Remove("NOSERIALNUMBER");
            }
        });
        lock (_lastV4TimestampLock)
        {
            _lastV4Timestamp = lastTs;
        }
    }

    private static bool TryParseDevice(JsonElement element, out V4Device device)
    {
        try
        {
            device = element.Deserialize<V4Device>(JsonOpts) ?? new V4Device();
            return true;
        }
        catch (JsonException)
        {
            device = new V4Device();
            return false;
        }
    }
}
