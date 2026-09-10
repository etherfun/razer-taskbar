// Port of src/watcher.rs (originally watcherV3.ts / watcherV4.ts /
// razer_watcher.ts).
//
// - V3: `%LOCALAPPDATA%/Razer/Synapse3/Log/Razer Synapse 3.log`
// - V4: `%LOCALAPPDATA%/Razer/RazerAppEngine/User Data/Logs/systray_systrayv2*.log`
//   the whole history is replayed; the last snapshot decides connection state.
// - battery_source="hid"/"auto": ParseOnce polls devices over raw HID
//   (HidWatcher) instead; auto falls back to the log parsing below when no
//   HID device answers.
// - FileSystemWatcher watches the files; a parse runs within 1s of any write
//   event and the poll loop re-parses every `polling_throttle_secs` as a
//   fallback (settings are re-read from disk each pass, so edits apply
//   without restart).
//
// .NET note: an unhandled exception on this thread kills the whole process
// (the Rust build isolates thread panics), so every tick is guarded.

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
    private readonly HidWatcher _hid = new();
    private readonly object _lastV4TimestampLock = new();
    private string _lastV4Timestamp = "";

    public RazerWatcher(DeviceStore devices)
    {
        _devices = devices;
    }

    /// <summary>Read a possibly-locked log file. Synapse keeps its logs open
    /// WITHOUT FileShare.Read, so File.ReadAllText (read + share-read) fails
    /// with "file in use"; Rust's fs::read_to_string opens fully shared and
    /// works. Mirror that: read share ReadWrite|Delete.</summary>
    private static string? ReadShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
        catch (Exception e)
        {
            Log.Info($"read failed ({Path.GetFileName(path)}): {e.Message}");
            return null;
        }
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

    /// <summary>Serial → device-name harvest from V4 snapshot text. The HID
    /// source uses this to give combo-dongle sub-devices their real model
    /// name (the USB product string names the dongle's primary device, and
    /// the vendor protocol exposes no name query). Last snapshot wins.</summary>
    public static Dictionary<string, string> ParseSerialNames(string logText)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in V4LineRegex.Matches(logText))
        {
            List<JsonElement> vals;
            try
            {
                vals = JsonSerializer.Deserialize<List<JsonElement>>(m.Groups["json"].Value, JsonOpts) ?? new();
            }
            catch (JsonException)
            {
                continue;
            }
            foreach (var v in vals)
            {
                string? serial = null;
                string? name = null;
                if (v.TryGetProperty("serialNumber", out var s) && s.ValueKind == JsonValueKind.String)
                {
                    serial = s.GetString();
                }
                if (v.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.Object
                    && n.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.String)
                {
                    name = en.GetString();
                }
                if (!string.IsNullOrEmpty(serial) && !string.IsNullOrEmpty(name))
                {
                    names[serial!] = name!;
                }
            }
        }
        return names;
    }

    /// <summary>One BLE battery device as Synapse's heartbeat device array
    /// describes it. Charging is null when the heartbeat is too stale to
    /// trust the flag (the identity itself is stable and always adopted).</summary>
    public sealed record BleIdentity(string Serial, string Name, string Category, bool? Charging);

    /// <summary>BLE ↔ profile bridge from the V4 heartbeat device arrays
    /// (`info: Device  [{…}, …]` lines). Synapse logs every paired device
    /// with its canonical serial regardless of transport — useBle marks the
    /// Bluetooth-LE ones (verified on a Joro: BLE session still logs
    /// serialNumber SI… with the USB productId). This upgrades the BLE MAC
    /// fallback identity to the real serial — one identity across dongle,
    /// cable and BT — and supplies the charging flag GATT lacks. Battery
    /// level stays with the live GATT read; only the last heartbeat is read.</summary>
    public static List<BleIdentity> HarvestBleIdentities()
    {
        var path = V4LogDir() is { } dir ? LatestV4Log(dir) : null;
        if (path is null)
        {
            return new List<BleIdentity>();
        }
        var tail = ReadTail(path, 256 * 1024);
        if (tail is null)
        {
            return new List<BleIdentity>();
        }
        string? heartbeat = null;
        foreach (var line in tail.Split('\n'))
        {
            if (line.Contains("\"useBle\""))
            {
                heartbeat = line;
            }
        }
        return heartbeat is null ? new List<BleIdentity>() : ParseBleHeartbeat(heartbeat, DateTime.Now);
    }

    internal static List<BleIdentity> ParseBleHeartbeat(string line, DateTime now)
    {
        var result = new List<BleIdentity>();
        var json = ExtractJsonArray(line);
        if (json is null)
        {
            return result;
        }
        // The heartbeat is only trusted for the charging flag while fresh;
        // Synapse writes one every ~a minute while it runs.
        bool chargingKnown = false;
        if (Regex.Match(line, @"^\[(?<ts>[^\]]+)\]") is { Success: true } ts
            && DateTime.TryParseExact(ts.Groups["ts"].Value, "yyyy/MM/dd HH:mm:ss.fff",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out var at))
        {
            chargingKnown = now - at < TimeSpan.FromMinutes(10);
        }
        List<JsonElement> vals;
        try
        {
            vals = JsonSerializer.Deserialize<List<JsonElement>>(json, JsonOpts) ?? new();
        }
        catch (JsonException)
        {
            return result;
        }
        foreach (var v in vals)
        {
            if (v.TryGetProperty("useBle", out var ble) && ble.ValueKind != JsonValueKind.True)
            {
                continue;
            }
            if (v.TryGetProperty("hasBattery", out var bat) && bat.ValueKind != JsonValueKind.True)
            {
                continue;
            }
            if (!v.TryGetProperty("serialNumber", out var s) || s.ValueKind != JsonValueKind.String
                || s.GetString() is not { Length: > 0 } serial)
            {
                continue;
            }
            var name = v.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.Object
                && n.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.String
                ? en.GetString() ?? ""
                : "";
            var category = v.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? ""
                : "";
            bool? charging = null;
            if (chargingKnown && v.TryGetProperty("powerStatus", out var p)
                && p.TryGetProperty("chargingStatus", out var cs) && cs.ValueKind == JsonValueKind.String)
            {
                charging = cs.GetString() == "Charging";
            }
            result.Add(new BleIdentity(serial, name, category, charging));
        }
        return result;
    }

    /// <summary>Pick the BLE identity for a GATT-only device. Matching by
    /// device kind; a single overall candidate is accepted even when the
    /// kinds disagree (the Bluetooth name may be terse, e.g. "Joro"). Two
    /// same-kind candidates stay ambiguous → null (MAC fallback identity).</summary>
    public static BleIdentity? MatchBleIdentity(List<BleIdentity> candidates, DeviceKind kind)
    {
        var byKind = candidates.Where(c => DeviceClassifier.FromCategoryAndName(c.Category, c.Name) == kind).ToList();
        if (byKind.Count == 1)
        {
            return byKind[0];
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>First top-level JSON array of the line, extracted by
    /// string-aware bracket matching. The leading "[timestamp]" bracket is
    /// skipped because it contains no '{' or '"'.</summary>
    internal static string? ExtractJsonArray(string line)
    {
        for (int i = line.IndexOf('['); i >= 0; i = line.IndexOf('[', i + 1))
        {
            int depth = 0;
            bool inString = false;
            for (int k = i; k < line.Length; k++)
            {
                char c = line[k];
                if (inString)
                {
                    if (c == '\\')
                    {
                        k++;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                }
                else if (c == '"')
                {
                    inString = true;
                }
                else if (c == '[')
                {
                    depth++;
                }
                else if (c == ']')
                {
                    if (--depth == 0)
                    {
                        var span = line[i..(k + 1)];
                        if (span.Contains('{') || span.Contains('"'))
                        {
                            return span;
                        }
                        break; // e.g. the "[2026/09/07 …]" timestamp bracket
                    }
                }
            }
        }
        return null;
    }

    /// <summary>Last `bytes` of a file, share-read (see ReadShared).</summary>
    private static string? ReadTail(string path, int bytes)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > bytes)
            {
                fs.Seek(-bytes, SeekOrigin.End);
            }
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
        catch (Exception e)
        {
            Log.Info($"tail read failed ({Path.GetFileName(path)}): {e.Message}");
            return null;
        }
    }

    /// <summary>Harvest from the latest V4 log on disk (Synapse must have run
    /// once — the log persists after it exits). Best effort.</summary>
    public static Dictionary<string, string> HarvestSerialNames()
    {
        var path = V4LogDir() is { } dir ? LatestV4Log(dir) : null;
        if (path is null)
        {
            return new Dictionary<string, string>();
        }
        var text = ReadShared(path);
        return text is null ? new Dictionary<string, string>() : ParseSerialNames(text);
    }

    public void Run(int initialPollSeconds)
    {
        try
        {
            RunLoop(initialPollSeconds);
        }
        catch (Exception e)
        {
            // .NET kills the whole process on an unhandled thread exception;
            // the Rust build keeps running (thread-local panic). Never die.
            Log.Error("watcher loop crashed", e);
        }
    }

    private void RunLoop(int initialPollSeconds)
    {
        // Initial parse so the widget shows something immediately.
        ParseOnce(ConfigService.Load());
        var initialDevices = _devices.Snapshot();
        var v4Path = V4LogDir() is { } d0 ? LatestV4Log(d0) : null;
        Log.Info(
            $"watcher initial parse: devices={initialDevices.Count}, v4log={(v4Path is null ? "none" : Path.GetFileName(v4Path))}, " +
            $"connected={initialDevices.Values.Count(d => d.IsConnected)}");

        long initialPollMs = initialPollSeconds * 1000L;
        long intervalMs = initialPollMs;
        long lastParse = Environment.TickCount64;
        long? dirtyAt = null;

        var signal = new SemaphoreSlim(0, int.MaxValue);
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
            try
            {
                Tick(signal, ref intervalMs, ref lastParse, ref dirtyAt);
            }
            catch (Exception e)
            {
                // One failed pass must not kill the thread (the process
                // survives only if the exception is handled here).
                Log.Error("watcher tick failed", e);
                lastParse = Environment.TickCount64;
                dirtyAt = null;
                Drain(signal);
                Thread.Sleep(1000);
            }
        }
    }

    private void Tick(SemaphoreSlim signal, ref long intervalMs, ref long lastParse, ref long? dirtyAt)
    {
        long now = Environment.TickCount64;
        long pollDue = lastParse + intervalMs;
        long due = dirtyAt is { } t ? Math.Min(t + (long)EventDebounce.TotalMilliseconds, pollDue) : pollDue;
        long waitMs = due - now;
        if (waitMs <= 0)
        {
            // One config read drives the parse, the record gate and the
            // throttle (a pass used to re-read settings.json 2-3 times).
            var cfg = ConfigService.Load();
            // Re-resolve the V4 log in case Synapse rotated to a new file.
            ParseOnce(cfg);
            // Record runs unconditionally: its disabled path is what retires
            // the frozen prediction cache (gating here left stale estimates
            // on the tray/hover after "record battery history" was turned
            // off).
            HistoryService.Record(_devices, cfg.RecordBatteryHistory);
            // Long-lived loop: re-read the throttle (edits apply live).
            // While recording, the tighter record interval wins. Rust used
            // Duration::from_secs — the seconds→ms conversion happens here.
            long poll = IntervalSecs(cfg.PollingThrottleSecs);
            long hist = IntervalSecs(cfg.HistoryPollIntervalSecs);
            intervalMs = cfg.RecordBatteryHistory && HistoryService.Ready()
                ? Math.Max(1, Math.Min(poll, hist)) * 1000L
                : Math.Max(2, poll) * 1000L;
            lastParse = Environment.TickCount64;
            dirtyAt = null;
            Drain(signal);
            return;
        }
        if (signal.Wait((int)Math.Min(waitMs, int.MaxValue - 1)))
        {
            Drain(signal);
            dirtyAt ??= Environment.TickCount64;
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

    /// <summary>Config seconds, capped inside the ulong domain before any
    /// long cast: a hand-edited settings.json value near ulong.MaxValue
    /// would wrap negative through (long) and collapse to the interval
    /// floor (busy loop). Ceiling ≈ 24.8 days, far past any real throttle;
    /// 0 never reaches here (Load() normalizes it to the default).</summary>
    private static long IntervalSecs(ulong secs)
        => (long)Math.Min(secs, (ulong)(int.MaxValue / 1000));

    private void ParseOnce(Config cfg)
    {
        // `cfg` was read once for this pass (edits apply live next pass).
        switch (cfg.BatterySource)
        {
            case "hid":
                _hid.Poll(_devices, cfg);
                return;
            case "log":
                ParseLog(cfg);
                return;
            default:
                // auto: direct HID wins whenever at least one battery device
                // answers (no Synapse needed); an empty poll falls back to
                // Synapse log parsing.
                if (_hid.Poll(_devices, cfg) > 0)
                {
                    return;
                }
                ParseLog(cfg);
                return;
        }
    }

    /// <summary>Synapse log parsing, selected by `synapse_version` (v3/v4/
    /// auto) — the original data source, unchanged.</summary>
    private void ParseLog(Config cfg)
    {
        switch (cfg.SynapseVersion)
        {
            case "v3": ParseV3(cfg); break;
            case "v4": ParseV4(cfg); break;
            default:
                // auto: V4 wins when its log dir has candidates (mirrors TS `auto`).
                var hasV4 = V4LogDir() is { } d && LatestV4Log(d) is not null;
                if (hasV4)
                {
                    ParseV4(cfg);
                }
                else
                {
                    ParseV3(cfg);
                }
                break;
        }
    }

    private void ParseV3(Config cfg)
    {
        var path = V3LogPath();
        if (path is null)
        {
            return;
        }
        var log = ReadShared(path);
        if (log is null)
        {
            return;
        }
        var snapshot = ParseV3Snapshot(log);
        var shown = cfg.ShownDeviceHandle;
        _devices.Mutate(devices =>
        {
            foreach (var (handle, name, level, charging, connected) in snapshot)
            {
                devices[handle] = new RazerDevice(
                    name, handle, level, charging,
                    BatterySaver: false,
                    IsConnected: connected,
                    IsSelected: shown.Length == 0 || shown == handle,
                    Kind: DeviceClassifier.FromCategoryAndName("", name));
            }
        });
    }

    /// <summary>V3 log text → last-battery-event-per-handle snapshot.
    /// Internal for tests: the three regexes must keep matching the real
    /// Synapse 3 log shapes (docs/agent-csharp.md "V3/V4 正则逐字保留").</summary>
    internal static List<(string Handle, string Name, int Level, bool Charging, bool Connected)> ParseV3Snapshot(string log)
    {
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

        var result = new List<(string, string, int, bool, bool)>();
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
            result.Add((handle, name, level, charging, connected));
        }
        return result;
    }

    private void ParseV4(Config cfg)
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
        var log = ReadShared(path);
        if (log is null)
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

        var shown = cfg.ShownDeviceHandle;
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
