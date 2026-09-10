// Direct-HID battery source (no Synapse required). Enumerates Razer
// (VID 0x1532) HID collections via setupapi, exchanges the OpenRazer-style
// 90-byte battery/charging feature reports through hid.dll, and mirrors the
// results into DeviceStore as regular RazerDevice entries so widget, hover
// panel, tray and history need no source-specific handling.
//
// Query rhythm follows the established open-source implementations
// (xzeldon/razer-battery-report, OpenRazer): send → sleep → read, resending
// a few times while the device reports busy / no-response (wireless links
// answer late).
//
// Windows quirks handled here (learned probing real hardware, see
// docs/agent-hid.md):
//  - The 90-byte vendor feature report usually lives on the mouse top-level
//    collection, which refuses GENERIC_READ|GENERIC_WRITE (exclusive to the
//    OS input stack) but opens fine write-only — feature IOCTLs are
//    FILE_ANY_ACCESS. OpenForFeature walks RW → W → R → query-only.
//  - Collections whose descriptor has no feature reports fail SetFeature
//    with ERROR_INVALID_FUNCTION (1); they are skipped and blacklisted per
//    pid (the choice is stable for a given path).
//  - Handles are kept open across polls (HidSession): reopening after an
//    idle gap intermittently fails, and the exchange needs no reopen.
//    Stale handles self-heal: unplug removes the device path, which closes
//    and re-enumerates the session.
//  - Declared FeatureReportByteLength can be 0 even on collections that
//    accept the 90-byte report; the declared length is only a buffer hint.

using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using RazerTaskbar.Core.Interop;

namespace RazerTaskbar.Core;

/// <summary>Result of querying one physical device over HID. Pure data —
/// the mapping to RazerDevice (ToRazerDevice) is unit-testable.</summary>
public sealed record HidDeviceReading(
    int ProductId,
    string ProductName,
    string Serial,
    byte LevelRaw,
    int LevelPercent,
    /// <summary>null = charging status unsupported (AA-battery devices).</summary>
    bool? IsCharging,
    /// <summary>Combo-dongle keyboards: the product string names the dongle's
    /// primary device, so the slot role supplies the real label/kind.</summary>
    string? NameOverride = null,
    DeviceKind? KindOverride = null);

public sealed class HidWatcher
{
    private const int QueryDelayMs = 60;   // send → sleep → read (xzeldon rhythm)
    private const int MaxAttempts = 5;     // busy/no-response resends
    // Wireless links idle-sleep: the first query after a gap wakes the device
    // but the answer needs time (xzeldon retries up to 10 × 500 ms). A waking
    // link answers within a few hundred ms; an actually absent device burns
    // the full budget once per poll on the watcher thread.
    private static readonly int[] RetryDelaysMs = { 100, 200, 350, 500 };
    private const int DisconnectAfterMisses = 2;
    private const int ReopenAfterFailStreak = 6; // polls; stale-handle failsafe

    private const int HidpStatusSuccess = 0x0011_0000;
    private const int ErrorInvalidFunction = 1;
    private const int ErrorInvalidParameter = 87; // BLE report maps reject the 91-byte vendor buffer

    /// <summary>VIDs Razer HID devices enumerate with: 0x1532 on USB and
    /// classic BT; 0x068E on BLE (Razer Joro BT — Synapse's own
    /// RZCONTROL\VID_068E node confirms it is a Razer vendor id).</summary>
    private static readonly HashSet<int> RazerVendorIds = new() { RazerReport.VendorId, RazerReport.BleVendorId };

    // USB interface paths: `hid#vid_1532&pid_00b8&mi_01&col05#…`. Bluetooth
    // paths use the `vid&…_pid&…` form with a variable-width VID: classic BT
    // writes 7 hex digits (`vid&0001532_pid&02cd`), BLE prefixes the vendor
    // id with its 2-digit id source (02 = Bluetooth SIG):
    // `vid&02068e_pid&02ce`. In both the VID lives in the last 4 hex digits.
    private static readonly Regex VidPidRegex = new(
        @"vid_(?<vid>[0-9a-f]{4})&pid_(?<pid>[0-9a-f]{4})|vid&(?<vidb>[0-9a-f]{6,8})_pid&(?<pidb>[0-9a-f]{4,8})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Watcher-thread-only bookkeeping (ParseOnce is the single caller).
    private string _lastSignature = "**uninitialized**";
    private HashSet<string> _owned = new();
    private readonly Dictionary<string, int> _misses = new();
    private readonly Dictionary<int, HidSession> _sessions = new();
    private readonly Dictionary<int, HashSet<string>> _deadPaths = new(); // pid → paths without feature reports
    private readonly Dictionary<int, int> _failStreak = new();

    /// <summary>One HID poll cycle. Returns the number of battery devices
    /// that answered (drives the auto-mode fallback to log parsing).</summary>
    public int Poll(DeviceStore devices, Config cfg)
    {
        List<HidDeviceReading> readings;
        string signature;
        try
        {
            var paths = EnumerateRazerInterfacePaths();
            readings = new List<HidDeviceReading>();
            foreach (var group in paths.GroupBy(p => p.Pid))
            {
                // BLE HID carries no vendor feature channel (its report map
                // rejects the 91-byte vendor buffer). Power comes from
                // Razer's private vendor GATT channel when it is free —
                // battery AND the charging flag, no heartbeat needed — and
                // from the plain GATT Battery Service otherwise. The MAC is
                // the same for every collection of the device.
                var bleMac = group.All(p => IsBlePath(p.Path)) ? BleMac(group.First().Path) : null;
                if (bleMac is { } mac)
                {
                    if (BleVendor.TryReadPower(mac) is { } power)
                    {
                        // Identity (serial/name) still rides the Synapse
                        // heartbeat bridge; the vendor serial read is a
                        // third transaction we skip at poll cadence.
                        var identity = BleIdentityFor(mac,
                            DeviceClassifier.FromCategoryAndName("", power.Name));
                        var serial = identity?.Serial ?? $"BLE:{mac:X12}";
                        var name = identity is { Name.Length: > 0 } ? identity.Name
                            : (power.Name.Length > 0 ? power.Name : null)
                            ?? "Razer Keyboard";
                        readings.Add(new HidDeviceReading(group.Key, "", serial,
                            power.RawBattery, BleVendor.BatteryPercent(power.RawBattery),
                            power.Charging, name,
                            DeviceClassifier.FromCategoryAndName(identity?.Category ?? "", name)));
                    }
                    else if (BleBattery.TryRead(mac) is { } ble)
                    {
                        // Identity bridge: Synapse's heartbeat device arrays
                        // log every paired device with its canonical serial
                        // regardless of transport (useBle marks the BLE
                        // ones). A match upgrades the MAC identity to the
                        // real serial — one identity across dongle/cable/BT
                        // — and supplies the charging flag GATT lacks.
                        var identity = BleIdentityFor(mac,
                            DeviceClassifier.FromCategoryAndName("", ble.Name));
                        var serial = identity?.Serial ?? ble.Serial ?? $"BLE:{mac:X12}";
                        var name = identity is { Name.Length: > 0 } ? identity.Name
                            : (ble.Serial is { } s ? HarvestedName(s) : null)
                            ?? (ble.Name.Length > 0 ? ble.Name : null)
                            ?? "Razer Keyboard";
                        readings.Add(new HidDeviceReading(group.Key, "", serial,
                            ble.Percent, ble.Percent, identity?.Charging, name,
                            DeviceClassifier.FromCategoryAndName(identity?.Category ?? "", name)));
                    }
                    continue;
                }
                try
                {
                    readings.AddRange(QueryPid(group.Key, group.Select(p => p.Path).ToList()));
                }
                catch (Exception e)
                {
                    Log.Error($"hid query failed (pid 0x{group.Key:X4})", e);
                }
            }
            // Devices gone entirely: drop their sessions (the next poll of a
            // replugged device starts fresh).
            foreach (var pid in _sessions.Keys.Where(pid => paths.All(p => p.Pid != pid)).ToList())
            {
                CloseSession(pid);
                _deadPaths.Remove(pid);
            }
            signature = string.Join("|", readings.Select(r => $"{r.ProductId:X4}/{r.Serial}").Order());
        }
        catch (Exception e)
        {
            Log.Error("hid poll failed", e);
            return 0;
        }
        if (signature != _lastSignature)
        {
            Log.Info("hid poll: " + (readings.Count == 0
                ? "no battery devices"
                : string.Join(", ", readings.Select(r => $"0x{r.ProductId:X4} \"{r.NameOverride ?? r.ProductName}\" {r.LevelPercent}%"))));
            _lastSignature = signature;
        }
        Commit(devices, readings, cfg.ShownDeviceHandle);
        return readings.Count;
    }

    /// <summary>Pure mapping into the shared device model: HID readings look
    /// exactly like Synapse-log entries downstream (selection stamping
    /// mirrors WatcherService.ParseV4).</summary>
    public static RazerDevice ToRazerDevice(HidDeviceReading reading, string handle, string shownHandle)
    {
        var name = reading.NameOverride
            ?? (reading.ProductName.Length > 0 ? reading.ProductName : $"Razer device 0x{reading.ProductId:X4}");
        // A wired device at 100% runs off USB power even when its charge flag
        // has dropped (the firmware pauses top-off at full) — present that
        // state as charging. Receiver slots and BLE never take this
        // shortcut: there the device sits on its battery at 100%.
        var charging = reading.IsCharging == true
            || (RazerPidTable.IsWiredDevice(reading.ProductId) && reading.LevelPercent >= 100);
        return new RazerDevice(
            name,
            handle,
            reading.LevelPercent,
            charging,
            BatterySaver: false, // not exposed by the vendor battery commands
            IsConnected: true,
            shownHandle.Length == 0 || shownHandle == handle,
            reading.KindOverride ?? DeviceClassifier.FromCategoryAndName("", reading.ProductName));
    }

    /// <summary>Handle key: the USB serial string when the device reports a
    /// real one (Synapse logs carry the same value as `serialNumber`);
    /// dongles report an all-zero serial, which falls back to a synthesized
    /// stable id.</summary>
    public static string HandleFor(HidDeviceReading reading)
    {
        var serial = reading.Serial.Trim();
        return serial.Length > 0 && !serial.All(c => c == '0') ? serial : $"HID:{reading.ProductId:X4}";
    }

    /// <summary>Identity bridge across the two battery sources. When the
    /// vendor serial query goes unanswered, HandleFor synthesizes a fallback
    /// (`HID:{pid}` / `BLE:{mac}`) — but the same physical device is usually
    /// already listed by the Synapse log under its real serial, and both
    /// identities would surface as two hover-panel rows and two history
    /// series. Fold the fallback reading into the same-name serial entry
    /// (HID battery values win, matching battery_source=auto's direct-read
    /// preference) and let the caller drop the stale fallback entry. Returns
    /// the effective handle plus the stale one (null = the fallback stands
    /// alone, e.g. Synapse not running). Two units of one model where BOTH
    /// fail serial resolution would merge into one row — accepted: serial
    /// resolution is the norm; the fallback is the exception.</summary>
    public static (string Handle, string? Stale) ResolveIdentity(
        IReadOnlyDictionary<string, RazerDevice> map,
        string handle,
        HidDeviceReading reading,
        IReadOnlySet<string> seen)
    {
        // Real serial (vendor answer or BLE identity bridge): the log source
        // carries the same value, the dictionary dedupes by itself.
        if (handle.IndexOf(':') < 0)
        {
            return (handle, null);
        }
        var name = reading.NameOverride ?? reading.ProductName;
        if (name.Length == 0)
        {
            return (handle, null);
        }
        string? twin = null;
        foreach (var (key, device) in map)
        {
            if (key == handle
                || key.Contains(':') // only serial identities from the log source
                || seen.Contains(key) // already claimed by a reading this round
                || !string.Equals(device.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            twin = key;
            break;
        }
        return twin is null ? (handle, null) : (twin, handle);
    }

    /// <summary>Same-round pid dedup: a slot whose battery answered but whose
    /// serial query failed yields the fallback identity `HID:{pid}` — and the
    /// same physical device may have answered with its real serial on another
    /// slot/transaction of the same pid in the same poll (dongles expose
    /// several transaction ids that all reach the mouse). The serial reading
    /// wins; the pid-shaped fallback of an already-resolved pid is dropped.
    /// Kept: fallbacks of pids with no serial answer this round (the device
    /// still needs its entry) and BLE MAC fallbacks (per-device, not
    /// pid-derived — a second same-model unit on the same BLE pid must not
    /// be swallowed).</summary>
    internal static List<HidDeviceReading> DedupRound(List<HidDeviceReading> readings)
    {
        var serialPids = new HashSet<int>();
        foreach (var r in readings)
        {
            if (HandleFor(r).IndexOf(':') < 0)
            {
                serialPids.Add(r.ProductId);
            }
        }
        List<HidDeviceReading>? result = null;
        for (int i = 0; i < readings.Count; i++)
        {
            var r = readings[i];
            var h = HandleFor(r);
            if (serialPids.Contains(r.ProductId)
                && h == $"HID:{r.ProductId:X4}")
            {
                result ??= new List<HidDeviceReading>(readings.Take(i));
                continue;
            }
            result?.Add(r);
        }
        return result ?? readings;
    }

    internal void Commit(DeviceStore devices, List<HidDeviceReading> readings, string shown)
    {
        readings = DedupRound(readings);
        // Alias merges touch SQLite; collect them and run after Mutate so
        // the DeviceStore lock never spans DB IO (Snapshot blocks on it).
        var aliases = new List<(string Src, string Dst)>();
        devices.Mutate(map =>
        {
            var seen = new HashSet<string>();
            foreach (var reading in readings)
            {
                var (handle, stale) = ResolveIdentity(map, HandleFor(reading), reading, seen);
                var merged = ToRazerDevice(reading, handle, shown);
                seen.Add(handle);
                // Same handle from two transports (cable + BT charging at
                // once): keep the entry that knows charging — GATT has no
                // charging flag, so a BLE reading must not erase it.
                if (map.TryGetValue(handle, out var previous)
                    && previous.IsCharging && !merged.IsCharging && reading.IsCharging != true)
                {
                    continue;
                }
                map[handle] = merged;
                if (stale is { } dead && dead != handle)
                {
                    map.Remove(dead);
                }
                // A serial that resolved after earlier fallback rounds retires
                // the fallback entry rooted while the radio was still waking
                // (cold start: ResolveIdentity had no serial twin to fold
                // into, so HID:{pid} took root and reached battery.db). The
                // pid-derived form is unambiguous — it is exactly what this
                // device's failed serial query produces. BLE:{mac} fallbacks
                // are not pid-derived; their rooted entries keep relying on
                // the name-based fold and the startup DB merge.
                if (handle.IndexOf(':') < 0)
                {
                    var rooted = $"HID:{reading.ProductId:X4}";
                    if (map.ContainsKey(rooted))
                    {
                        map.Remove(rooted);
                        _misses.Remove(rooted);
                        Log.Info($"hid: serial resolved, retiring rooted fallback {rooted} -> {handle}");
                        aliases.Add((rooted, handle));
                    }
                }
            }
            // This source only manages connectivity of the entries it wrote:
            // a device missing from this round starts a miss counter (dongle
            // still enumerated, device powered off) and is marked
            // disconnected after N consecutive misses.
            foreach (var handle in _owned)
            {
                if (seen.Contains(handle)
                    || !map.TryGetValue(handle, out var device)
                    || !device.IsConnected)
                {
                    continue;
                }
                _misses[handle] = _misses.GetValueOrDefault(handle) + 1;
                if (_misses[handle] >= DisconnectAfterMisses)
                {
                    map[handle] = device with { IsConnected = false };
                }
            }
            foreach (var handle in seen)
            {
                _misses.Remove(handle);
            }
            _owned = seen;
        });
        foreach (var (src, dst) in aliases)
        {
            HistoryService.MergeAlias(src, dst);
        }
    }

    // — device discovery —

    internal sealed record HidInterface(
        string Path, int Pid, ushort UsagePage, ushort Usage,
        ushort InputLength, ushort OutputLength, ushort FeatureLength,
        string Product, string Serial);

    /// <summary>An open collection kept across polls. The exchange never
    /// reopens; only replug (path gone) or a persistent failure streak does.</summary>
    private sealed class HidSession
    {
        public required IntPtr Handle;
        public required string Path;
        public required HidInterface Iface;
        /// <summary>Vendor serials per transaction id (multi-device dongles
        /// host several sub-devices on one collection). Empty when the
        /// device refuses the vendor command.</summary>
        public readonly Dictionary<byte, string> SerialByTx = new();
    }

    /// <summary>All present Razer HID collection paths (USB + Bluetooth
    /// path forms), without opening them.</summary>
    internal static List<(string Path, int Pid)> EnumerateRazerInterfacePaths()
    {
        var list = new List<(string, int)>();
        var guid = HidApi.GuidDevinterfaceHid;
        var set = HidApi.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero,
            HidApi.DIGCF_PRESENT | HidApi.DIGCF_DEVICEINTERFACE);
        if (!HidApi.IsValid(set))
        {
            Log.Error("SetupDiGetClassDevs(GUID_DEVINTERFACE_HID) failed");
            return list;
        }
        try
        {
            for (int i = 0; ; i++)
            {
                var ifData = new HidApi.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<HidApi.SP_DEVICE_INTERFACE_DATA>() };
                if (!HidApi.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifData))
                {
                    break; // ERROR_NO_MORE_ITEMS
                }
                var path = DetailPath(set, ifData);
                if (path is null || VidPidRegex.Match(path) is not { Success: true } m)
                {
                    continue;
                }
                var vidHex = m.Groups["vid"].Success
                    ? m.Groups["vid"].Value
                    : m.Groups["vidb"].Value[^4..]; // BT forms: VID = last 4 hex digits
                var pidHex = m.Groups["pid"].Success ? m.Groups["pid"].Value : m.Groups["pidb"].Value;
                if (!RazerVendorIds.Contains(Convert.ToInt32(vidHex, 16)))
                {
                    continue;
                }
                list.Add((path, Convert.ToInt32(pidHex, 16)));
            }
        }
        finally
        {
            HidApi.SetupDiDestroyDeviceInfoList(set);
        }
        return list;
    }

    /// <summary>Two-call SetupDiGetDeviceInterfaceDetailW dance; returns the
    /// symbolic link path or null.</summary>
    private static string? DetailPath(IntPtr set, HidApi.SP_DEVICE_INTERFACE_DATA ifData)
    {
        try
        {
            if (!HidApi.SetupDiGetDeviceInterfaceDetailW(set, ref ifData, IntPtr.Zero, 0, out var size, IntPtr.Zero)
                && size == 0)
            {
                return null;
            }
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize must be the native
                // struct size: 8 on x64, 6 on x86 (classic setupapi gotcha).
                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                if (!HidApi.SetupDiGetDeviceInterfaceDetailW(set, ref ifData, buffer, size, out _, IntPtr.Zero))
                {
                    return null;
                }
                return Marshal.PtrToStringUni(buffer + sizeof(int));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception e)
        {
            // The HID source quietly disappears without this path (auto mode
            // falls back to log parsing) — keep the null contract, add the trace.
            Log.Error("HID interface detail path enumeration failed", e);
            return null;
        }
    }

    // BLE HID interfaces (HID-over-GATT) carry `{00001812-…}` — the HID
    // service UUID — instead of `vid_xxxx&pid_xxxx&mi_xx`; the BLE MAC
    // precedes `&colNN`.
    internal static bool IsBlePath(string path)
        => path.Contains("{00001812-", StringComparison.OrdinalIgnoreCase);

    internal static readonly Regex BleMacRegex = new(
        @"_(?<mac>[0-9a-f]{12})&col\d",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static ulong? BleMac(string path)
        => BleMacRegex.Match(path) is { Success: true } m
            ? Convert.ToUInt64(m.Groups["mac"].Value, 16)
            : null;

    internal static HidInterface? DescribeHandle(IntPtr handle, string path, int pid)
    {
        if (!HidApi.HidD_GetPreparsedData(handle, out var preparsed))
        {
            return null;
        }
        try
        {
            var caps = new HidApi.HIDP_CAPS { Reserved = new ushort[17] };
            if (HidApi.HidP_GetCaps(preparsed, ref caps) != HidpStatusSuccess)
            {
                return null;
            }
            return new HidInterface(path, pid, caps.UsagePage, caps.Usage,
                caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength,
                HidApi.GetString(handle, serial: false), HidApi.GetString(handle, serial: true));
        }
        finally
        {
            HidApi.HidD_FreePreparsedData(preparsed);
        }
    }

    // — query —

    /// <summary>Query one physical USB device — which on multi-device dongles
    /// hosts several wireless sub-devices (one reading per answered slot).
    /// Reuses the open session, falling back to the remaining collections
    /// (blacklisted descriptor-less paths skipped) when the cached one
    /// proves unusable.</summary>
    private List<HidDeviceReading> QueryPid(int pid, List<string> paths)
    {
        _workingPath.TryGetValue(pid, out var cached);
        // Cached winner first, then the rest in path order (the root mouse
        // collection that carries the vendor report sorts early).
        var ordered = (cached is not null ? new[] { cached } : Array.Empty<string>())
            .Concat(paths.Where(p => p != cached).Order(StringComparer.OrdinalIgnoreCase));

        foreach (var path in ordered)
        {
            if (_deadPaths.TryGetValue(pid, out var dead) && dead.Contains(path))
            {
                continue; // descriptor has no feature reports — settled
            }
            var session = GetOrOpenSession(pid, path);
            if (session is null)
            {
                continue;
            }
            _collectionUnusable = false;
            var readings = ExchangeSlots(session, pid);
            if (readings.Count > 0)
            {
                _workingPath[pid] = path;
                _failStreak[pid] = 0;
                return readings;
            }
            if (_collectionUnusable)
            {
                // SetFeature said ERROR_INVALID_FUNCTION: this collection can
                // never carry the vendor report. Skip it permanently.
                CloseSession(pid);
                _deadPaths.GetOrAddNew(pid).Add(path);
                continue;
            }
            // Device not answering right now (powered off / wireless asleep)
            // or a stale handle. Keep the handle; a stale one is detected by
            // a persistent failure streak, a replug by the path check.
            var streak = _failStreak[pid] = _failStreak.GetValueOrDefault(pid) + 1;
            if (streak >= ReopenAfterFailStreak)
            {
                Log.Info($"hid: no answer {streak} polls in a row, reopening …{path[^40..]}");
                CloseSession(pid);
                _failStreak[pid] = 0;
            }
            return readings;
        }
        return new List<HidDeviceReading>();
    }

    private bool _collectionUnusable; // set by QueryCommand (watcher thread only)
    private readonly Dictionary<int, string> _workingPath = new();
    // Serial → model name, opportunistically harvested from the Synapse V4
    // log (it persists after Synapse exits). Purely a display bonus.
    private Dictionary<string, string> _harvestedNames = new();
    private long _harvestAtMs = -1;
    private const int HarvestRetryMs = 10 * 60 * 1000;

    private List<HidDeviceReading> ExchangeSlots(HidSession session, int pid)
    {
        var readings = new List<HidDeviceReading>();
        var seenHandles = new HashSet<string>();
        foreach (var slot in RazerPidTable.DeviceSlots(pid))
        {
            // Secondary slots (a paired keyboard on a combo dongle) get a
            // short budget: they are usually absent, and NoResponse storms
            // on the shared radio must not stretch the poll.
            var attempts = slot.Role == RazerPidTable.SlotRole.Keyboard ? 2 : MaxAttempts;
            if (QueryCommand(session.Handle, session.Iface.FeatureLength,
                    RazerReport.BuildBatteryQuery(slot.TransactionId), attempts) is not { } levelRaw)
            {
                if (_collectionUnusable)
                {
                    break; // collection can't carry feature reports at all
                }
                continue; // slot absent / asleep — try the next one
            }
            // Charging is optional: AA-battery devices fail this command and
            // report null (displayed as not charging).
            var chargingRaw = QueryCommand(session.Handle, session.Iface.FeatureLength,
                RazerReport.BuildChargingQuery(slot.TransactionId), attempts);
            var charging = chargingRaw is { } raw ? raw != 0 : (bool?)null;
            var vendorSerial = SerialFor(session, slot.TransactionId);
            var serial = vendorSerial.Length > 0 ? vendorSerial : session.Iface.Serial;
            var product = session.Iface.Product;
            var keyboardSlot = slot.Role == RazerPidTable.SlotRole.Keyboard;
            string? nameOverride = null;
            DeviceKind? kindOverride = keyboardSlot ? DeviceKind.Keyboard : null;
            if (keyboardSlot && DeviceClassifier.FromCategoryAndName("", product) != DeviceKind.Keyboard)
            {
                // Combo dongle: the USB product string names the mouse, and
                // the vendor protocol has no name query — use the real model
                // name harvested from the Synapse log when one exists.
                nameOverride = HarvestedName(serial) ?? "Razer Keyboard";
            }
            var reading = new HidDeviceReading(pid, product, serial, levelRaw,
                RazerReport.LevelPercent(levelRaw, slot.Scale), charging, nameOverride, kindOverride);
            // Serial-keyed dedup: transaction ids that route to the same
            // sub-device (0x1F/0x3F/0x0F all answered for the mouse) collapse.
            if (seenHandles.Add(HandleFor(reading)))
            {
                readings.Add(reading);
            }
        }
        return readings;
    }

    private string? HarvestedName(string serial)
    {
        if (serial.Length == 0)
        {
            return null;
        }
        if (_harvestedNames.TryGetValue(serial, out var name))
        {
            return name;
        }
        long now = Environment.TickCount64;
        if (_harvestAtMs < 0 || now - _harvestAtMs > HarvestRetryMs)
        {
            _harvestAtMs = now;
            try
            {
                _harvestedNames = RazerWatcher.HarvestSerialNames();
            }
            catch (Exception e)
            {
                Log.Error("hid: serial-name harvest failed", e);
            }
            if (_harvestedNames.TryGetValue(serial, out name))
            {
                return name;
            }
        }
        return null;
    }

    private List<RazerWatcher.BleIdentity> _bleIdentities = new();
    private long _bleIdentityAtMs = -1;
    private const int BleIdentityRefreshMs = 60 * 1000;

    /// <summary>Cached BLE identity bridge lookup (Synapse heartbeat log,
    /// refreshed at most once a minute — the heartbeat itself is written at
    /// about that rate). Matches by the Bluetooth-name kind; returns null
    /// when nothing matches unambiguously — the caller then falls back to
    /// the BLE MAC identity.</summary>
    private RazerWatcher.BleIdentity? BleIdentityFor(ulong mac, DeviceKind kind)
    {
        long now = Environment.TickCount64;
        if (_bleIdentityAtMs < 0 || now - _bleIdentityAtMs > BleIdentityRefreshMs)
        {
            _bleIdentityAtMs = now;
            try
            {
                _bleIdentities = RazerWatcher.HarvestBleIdentities();
            }
            catch (Exception e)
            {
                Log.Error("hid: BLE identity harvest failed", e);
            }
        }
        return _bleIdentities.Count == 0
            ? null
            : RazerWatcher.MatchBleIdentity(_bleIdentities, kind);
    }

    /// <summary>Vendor serial per transaction id, resolved once and cached.
    /// Each paired device answers with the exact serialNumber Synapse logs
    /// carry — one identity across both data sources. An unresolved serial
    /// (opened during a radio storm) is NOT cached: it retries every poll,
    /// otherwise the fallback `HID:{pid}` identity would take root.</summary>
    private string SerialFor(HidSession session, byte tx)
    {
        if (session.SerialByTx.TryGetValue(tx, out var cached) && cached.Length > 0)
        {
            return cached;
        }
        var serial = QuerySerial(session.Handle, session.Iface.FeatureLength, RazerReport.BuildSerialQuery(tx)) ?? "";
        if (serial.Length > 0)
        {
            session.SerialByTx[tx] = serial;
        }
        return serial;
    }

    private HidSession? GetOrOpenSession(int pid, string path)
    {
        if (_sessions.TryGetValue(pid, out var existing))
        {
            if (existing.Path == path)
            {
                return existing;
            }
            CloseSession(pid); // cached path differs (blacklisted?): open the requested one
        }
        var handle = HidApi.OpenForFeature(path, out _);
        if (!HidApi.IsValid(handle))
        {
            Log.Info($"hid open failed for every access mode: …{path[^40..]}");
            return null;
        }
        if (DescribeHandle(handle, path, pid) is not { } iface)
        {
            Log.Info($"hid caps unreadable: …{path[^40..]}");
            HidApi.CloseHandle(handle);
            return null;
        }
        var session = new HidSession { Handle = handle, Path = path, Iface = iface };
        _sessions[pid] = session;
        // Sub-device serials are queried lazily per slot (SerialFor): dongles
        // report an all-zero HID serial string, but each paired device
        // answers the vendor serial query on its own transaction id with the
        // exact serialNumber Synapse logs carry.
        return session;
    }

    private void CloseSession(int pid)
    {
        if (_sessions.Remove(pid, out var session))
        {
            HidApi.CloseHandle(session.Handle);
        }
    }

    /// <summary>Send one get-command and read the answer, resending while
    /// the device reports busy / no-response. Returns arguments[1] or null.</summary>
    private byte? QueryCommand(IntPtr handle, ushort featureLength, byte[] query, int attempts = MaxAttempts)
    {
        // The declared length can be 0; the exchange itself decides whether
        // the collection accepts the 90-byte vendor report.
        var bufferLength = Math.Max((int)featureLength, RazerReport.HidBufferSize);
        var commandId = query[8]; // hid buffer: class@7, id@8, tx@2
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            var send = PadTo(query, bufferLength);
            if (!HidApi.HidD_SetFeature(handle, send, (uint)send.Length))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ErrorInvalidFunction || err == ErrorInvalidParameter)
                {
                    // Deterministic: the collection's report map has no 90-byte
                    // vendor report (no feature reports at all, or — BLE — a
                    // fixed-size standard one). The caller blacklists the path.
                    _collectionUnusable = true;
                    return null;
                }
                Log.Info($"hid setFeature failed err={err} (attempt {attempt + 1}/{MaxAttempts})");
                Thread.Sleep(30);
                continue;
            }
            Thread.Sleep(QueryDelayMs);
            var recv = new byte[bufferLength]; // zero-initialized: report id byte 0
            if (!HidApi.HidD_GetFeature(handle, recv, (uint)recv.Length))
            {
                Log.Info($"hid getFeature failed err={Marshal.GetLastWin32Error()} (attempt {attempt + 1}/{MaxAttempts})");
            }
            else
            {
                switch (RazerReport.ParseResponse(recv, query[2], query[7], commandId, out var argument1))
                {
                    case RazerResponseKind.Success:
                        return argument1;
                    case RazerResponseKind.Busy:
                    case RazerResponseKind.NoResponse:
                    case RazerResponseKind.BadEcho:
                    case RazerResponseKind.BadCrc:
                    case RazerResponseKind.BadLength:
                        break; // link still waking / transient — resend after a wait
                    case RazerResponseKind.NotSupported:
                        // Devices without a charging command (AA-battery mice)
                        // answer 0x05 every poll — only surface the battery
                        // query, the charging one is expected to fail.
                        if (query[8] == RazerReport.CommandGetBatteryLevel)
                        {
                            Log.Info($"hid: device reported status=0x{recv[1]:X2} for the battery query, giving up");
                        }
                        return null; // device doesn't implement this command
                }
            }
            if (attempt < RetryDelaysMs.Length)
            {
                Thread.Sleep(RetryDelaysMs[attempt]); // let the link wake, then resend
            }
        }
        return null;
    }

    /// <summary>Serial query (class 0x00/0x82): same exchange rhythm as the
    /// battery command, answer extracted from arguments[0..21].</summary>
    private static string? QuerySerial(IntPtr handle, ushort featureLength, byte[] query)
    {
        var bufferLength = Math.Max((int)featureLength, RazerReport.HidBufferSize);
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var send = PadTo(query, bufferLength);
            if (!HidApi.HidD_SetFeature(handle, send, (uint)send.Length))
            {
                var err = Marshal.GetLastWin32Error();
                if (err is ErrorInvalidFunction or ErrorInvalidParameter)
                {
                    return null; // collection can't carry feature reports
                }
                Thread.Sleep(30);
                continue;
            }
            Thread.Sleep(QueryDelayMs);
            var recv = new byte[bufferLength];
            if (!HidApi.HidD_GetFeature(handle, recv, (uint)recv.Length))
            {
                if (attempt < RetryDelaysMs.Length)
                {
                    Thread.Sleep(RetryDelaysMs[attempt]);
                }
                continue;
            }
            switch (RazerReport.ParseResponse(recv, query[2], query[7], query[8], out _))
            {
                case RazerResponseKind.Success:
                    return RazerReport.TryGetSerial(recv, query[2], out var serial) ? serial : null;
                case RazerResponseKind.NotSupported:
                    return null;
            }
            if (attempt < RetryDelaysMs.Length)
            {
                Thread.Sleep(RetryDelaysMs[attempt]);
            }
        }
        return null;
    }

    private static byte[] PadTo(byte[] data, int length)
    {
        if (length <= data.Length)
        {
            return data;
        }
        var padded = new byte[length];
        Array.Copy(data, padded, data.Length);
        return padded;
    }
}

/// <summary>`RazerTaskbar --hid-probe`: standalone diagnostic. Enumerates all
/// Razer HID collections — including the ones that refuse to open — dumps
/// their caps, then exercises battery and charging queries with full
/// request/response hex dumps. Output goes to the console (when one is
/// attached) and to %APPDATA%/razer-taskbar/hid-probe.log.</summary>
public static class HidProbe
{
    public static int Run(string[] args)
    {
        AttachParentConsole();
        var scanMode = args.Contains("--hid-scan");
        var lines = new List<string>();
        StreamWriter? stream = null;
        var dir = Path.Combine(
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPDATA")) ? "." : Environment.GetEnvironmentVariable("APPDATA")!,
            "razer-taskbar");
        Directory.CreateDirectory(dir);
        var logPath = Path.Combine(dir, scanMode ? "hid-scan.log" : "hid-probe.log");
        // The scan runs for minutes: stream every line to the file so
        // progress is observable while it runs.
        if (scanMode)
        {
            stream = new StreamWriter(logPath, append: false) { AutoFlush = true };
        }
        void Out(string s)
        {
            lines.Add(s);
            Console.WriteLine(s);
            stream?.WriteLine(s);
        }

        Out($"hid-probe {DateTime.Now:yyyy-MM-dd HH:mm:ss}{(scanMode ? " [get-half scan]" : "")}");
        var paths = HidWatcher.EnumerateRazerInterfacePaths();
        Out($"razer HID collections: {paths.Count}");

        if (scanMode)
        {
            // Aggressive read-only sweep of the vendor command space. Safety
            // triage (docs/agent-hid.md): set-half ids (0x00-0x7F) excluded —
            // every known destructive command lives there (pairing 0x00/0x41+
            // 0x46, unpair 0x00/0x42, charge control 0x07/0x10, factory-test
            // 0x02/0x00, LED matrix writes 0x03/0x0B+0x0C) — plus the
            // undocumented get-mirrors of the pairing commands (0x00/0xC1,
            // 0xC2, 0xC6). All probes carry zero arguments, data_size 2.
            GetHalfScan(paths, Out);
        }
        else
        {
            foreach (var group in paths.GroupBy(p => p.Pid))
            {
                var bleMac = group.All(p => HidWatcher.IsBlePath(p.Path))
                    ? HidWatcher.BleMac(group.First().Path)
                    : null;
                if (bleMac is { } mac)
                {
                    BleSweep(group.Key, mac, Out);
                    continue;
                }
                QueryPidVerbose(group.Key, group.Select(p => p.Path).ToList(), Out);
            }
            if (paths.Count == 0)
            {
                Out("no Razer (VID 0x1532/0x068E) HID interfaces found");
            }
            // Multi-device dongles (mouse + keyboard sharing one receiver) expose
            // one USB device; the paired keyboard may be addressable on the same
            // vendor feature channel via its own transaction id / argument.
            ComboSweep(paths, Out);
            NameSweep(paths, Out);
            SniffInputReports(paths, Out);
        }

        if (stream is not null)
        {
            stream.Dispose();
        }
        else
        {
            File.WriteAllText(logPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        }
        Console.WriteLine($"[written to {logPath}]");
        return 0;
    }

    /// <summary>Two-stage read-only sweep: probe every class with two common
    /// get ids, then sweep the full get-half (0x80-0xFF) of the classes that
    /// responded.</summary>
    private static void GetHalfScan(List<(string Path, int Pid)> paths, Action<string> out_)
    {
        foreach (var path in paths.Select(p => p.Path).Order(StringComparer.OrdinalIgnoreCase))
        {
            var handle = HidApi.OpenForFeature(path, out _);
            if (!HidApi.IsValid(handle) || HidWatcher.DescribeHandle(handle, path, 0) is not { } caps
                || caps.FeatureLength < RazerReport.HidBufferSize)
            {
                continue;
            }
            if (QueryRaw(handle, RazerReport.BuildBatteryQuery(0x1F), out _) != RazerResponseKind.Success)
            {
                HidApi.CloseHandle(handle);
                continue;
            }
            out_($"— get-half scan on …{path[^48..]} —");
            out_("  excluded: set-half 0x00-0x7F (pairing 0x41/0x46, unpair 0x42, …), class 0x00 ids 0xC1/0xC2/0xC6");
            foreach (var tx in new byte[] { 0x9F, 0x1F })
            {
                out_($"  tx=0x{tx:X2}: probing 256 classes…");
                var alive = new List<byte>();
                for (int cls = 0; cls <= 0xFF; cls++)
                {
                    if (cls % 16 == 0)
                    {
                        out_($"    probing class 0x{cls:X2}…");
                    }
                    foreach (var probeId in new byte[] { 0x80, 0x84 })
                    {
                        var kind = QueryRaw(handle, RazerReport.BuildQuery(tx, (byte)cls, probeId, 0x02), out _, attempts: 1, waitMs: 80);
                        if (kind != RazerResponseKind.NotSupported)
                        {
                            alive.Add((byte)cls);
                            out_($"    class 0x{cls:X2} alive (id 0x{probeId:X2} → {kind})");
                            break;
                        }
                    }
                }
                out_($"  tx=0x{tx:X2} alive classes: {(alive.Count == 0 ? "(none)" : string.Join(" ", alive.Select(c => $"0x{c:X2}")))}");
                foreach (var cls in alive)
                {
                    out_($"  tx=0x{tx:X2} sweeping class 0x{cls:X2}…");
                    var found = 0;
                    for (int id = 0x80; id <= 0xFF; id++)
                    {
                        if (cls == 0x00 && (id == 0xC1 || id == 0xC2 || id == 0xC6))
                        {
                            continue; // pairing-command get-mirrors: not worth the risk
                        }
                        var kind = QueryRaw(handle, RazerReport.BuildQuery(tx, (byte)cls, (byte)id, 0x02), out var recv, attempts: 2);
                        if (kind == RazerResponseKind.Success)
                        {
                            found++;
                            var text = PrintableRun(recv);
                            out_($"    cls 0x{cls:X2} id 0x{id:X2}: {Convert.ToHexString(recv, 1, 16)}{(text.Length > 0 ? $" \"{text}\"" : "")}");
                        }
                        else if (kind != RazerResponseKind.NotSupported)
                        {
                            out_($"    cls 0x{cls:X2} id 0x{id:X2}: {kind}");
                        }
                    }
                    out_($"    cls 0x{cls:X2}: {found} readable ids");
                }
            }
            HidApi.CloseHandle(handle);
            return;
        }
        out_("— get-half scan: no talking collection —");
    }

    /// <summary>BLE HID exposes no vendor feature channel; the useful data
    /// lives in the GATT services. Dump battery + device-info serial, then
    /// every service/characteristic with a printable value — a Razer vendor
    /// GATT service carrying the serial would let BT mode share the dongle/
    /// cable identity instead of falling back to the BLE MAC.</summary>
    private static void BleSweep(int pid, ulong mac, Action<string> out_)
    {
        out_($"— pid 0x{pid:X4} (BLE {mac:X12}) —");
        if (BleBattery.TryRead(mac) is not { } reading)
        {
            out_("  GATT battery read failed");
            return;
        }
        out_($"  battery: {reading.Percent}%");
        out_($"  serial:  {(reading.Serial is { } s ? s : "<no Device Information serial → identity falls back to BLE MAC>")}");
        out_($"  name:    \"{reading.Name}\"");
        foreach (var (service, characteristic, props, value) in BleBattery.DumpCharacteristics(mac))
        {
            if (value is { Length: > 0 } bytes)
            {
                var text = BleBattery.Printable(bytes);
                out_($"  svc {service} char {characteristic} [{props}]: {Convert.ToHexString(bytes)}{(text.Length > 0 ? $" \"{text}\"" : "")}");
            }
            else
            {
                out_($"  svc {service} char {characteristic} [{props}]: <unreadable>");
            }
        }
    }

    private static void QueryPidVerbose(int pid, List<string> paths, Action<string> out_)
    {
        var knownLabel = RazerPidTable.TryGetDevice(pid, out var known)
            ? $"known tx=0x{known.TransactionId:X2}"
            : "unknown pid, probing";
        out_($"— pid 0x{pid:X4} ({knownLabel}) —");
        foreach (var path in paths)
        {
            out_($"  path={path}");
            foreach (var access in new[] { "RW", "W", "R", "0" })
            {
                var handle = HidApi.Open(path, AccessMask(access));
                if (HidApi.IsValid(handle))
                {
                    if (TryQueries(handle, pid, $"access={access}", out_))
                    {
                        HidApi.CloseHandle(handle);
                        return; // this collection talked; no need for the others
                    }
                    HidApi.CloseHandle(handle);
                    break; // handle opened but the collection won't talk; try the next one
                }
                out_($"    open {access}: err={Marshal.GetLastWin32Error()}");
            }
        }
        out_("  no interface answered");
    }

    private static uint AccessMask(string mode) => mode switch
    {
        "RW" => HidApi.GENERIC_READ | HidApi.GENERIC_WRITE,
        "W" => HidApi.GENERIC_WRITE,
        "R" => HidApi.GENERIC_READ,
        _ => 0, // query-only handle; feature IOCTLs are FILE_ANY_ACCESS
    };

    private static bool TryQueries(IntPtr handle, int pid, string label, Action<string> out_)
    {
        if (HidWatcher.DescribeHandle(handle, path: "", pid) is not { } iface)
        {
            out_("    " + label + ": opened, but caps unreadable");
            return false;
        }
        out_("    " + label + $": usagePage=0x{iface.UsagePage:X4} usage=0x{iface.Usage:X4} in={iface.InputLength} " +
             $"out={iface.OutputLength} feature={iface.FeatureLength} product=\"{iface.Product}\" serial=\"{iface.Serial}\"");
        foreach (var tx in RazerPidTable.TransactionIdCandidates(pid))
        {
            var sreq = RazerReport.BuildSerialQuery(tx);
            out_($"    serial query tx=0x{tx:X2}: send={Hex(sreq)}");
            var serial = QueryVerboseSerial(handle, sreq, out_);
            out_($"      => serial=\"{(serial is { } s && s.Length > 0 ? s : "<no valid response>")}\"");
            var req = RazerReport.BuildBatteryQuery(tx);
            out_($"    battery query tx=0x{tx:X2}: send={Hex(req)}");
            var level = QueryVerbose(handle, req, out_);
            if (level is not { } raw)
            {
                out_("      => no valid response");
                continue;
            }
            out_($"      => raw={raw}  percent(scaled x100/255)={raw * 100 / 255}  percent(direct)={raw}");
            var creq = RazerReport.BuildChargingQuery(tx);
            out_($"    charging query tx=0x{tx:X2}: send={Hex(creq)}");
            var charging = QueryVerbose(handle, creq, out_);
            out_($"      => {(charging is { } c ? (c != 0 ? "CHARGING" : "not charging") : "unsupported / no valid response")}");
            return true;
        }
        return false;
    }

    private static byte? QueryVerbose(IntPtr handle, byte[] query, Action<string> out_, bool parseSerial = false)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            var sent = HidApi.HidD_SetFeature(handle, query, (uint)query.Length);
            if (!sent)
            {
                out_($"      attempt {attempt}: setFeature FAILED err={Marshal.GetLastWin32Error()}");
                return null;
            }
            out_($"      attempt {attempt}: setFeature ok ({query.Length} bytes)");
            Thread.Sleep(60);
            var recv = new byte[RazerReport.HidBufferSize];
            var got = HidApi.HidD_GetFeature(handle, recv, (uint)recv.Length);
            if (!got)
            {
                out_($"      attempt {attempt}: getFeature FAILED err={Marshal.GetLastWin32Error()}");
                continue;
            }
            out_($"      attempt {attempt}: getFeature ok recv={Hex(recv)}");
            var kind = RazerReport.ParseResponse(recv, query[2], query[7], query[8], out var arg1);
            out_($"      attempt {attempt}: parse={kind}{(kind == RazerResponseKind.Success ? $" arg1={arg1}" : "")}");
            if (kind == RazerResponseKind.Success)
            {
                return arg1;
            }
            if (kind == RazerResponseKind.NotSupported)
            {
                return null;
            }
        }
        return null;
    }

    private static string? QueryVerboseSerial(IntPtr handle, byte[] query, Action<string> out_)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            var sent = HidApi.HidD_SetFeature(handle, query, (uint)query.Length);
            if (!sent)
            {
                out_($"      attempt {attempt}: setFeature FAILED err={Marshal.GetLastWin32Error()}");
                return null;
            }
            out_($"      attempt {attempt}: setFeature ok ({query.Length} bytes)");
            Thread.Sleep(60);
            var recv = new byte[RazerReport.HidBufferSize];
            var got = HidApi.HidD_GetFeature(handle, recv, (uint)recv.Length);
            if (!got)
            {
                out_($"      attempt {attempt}: getFeature FAILED err={Marshal.GetLastWin32Error()}");
                continue;
            }
            out_($"      attempt {attempt}: getFeature ok recv={Hex(recv)}");
            var kind = RazerReport.ParseResponse(recv, query[2], query[7], query[8], out _);
            out_($"      attempt {attempt}: parse={kind}");
            if (kind == RazerResponseKind.Success)
            {
                return RazerReport.TryGetSerial(recv, query[2], out var serial) ? serial : null;
            }
            if (kind == RazerResponseKind.NotSupported)
            {
                return null;
            }
        }
        return null;
    }

    private static string Hex(byte[] data)
        => string.Join(" ", data.Take(12).Select(b => b.ToString("X2"))) + (data.Length > 12 ? " …" : "");

    /// <summary>On the collection that already talks (the mouse root), sweep
    /// the other wireless transaction ids and a device-index argument —
    /// multi-device dongles may route the paired keyboard through the same
    /// vendor feature channel. A serial answer starting "SI" would identify
    /// the Joro (its Synapse serial is SI2522F18701637).</summary>
    private static void ComboSweep(List<(string Path, int Pid)> paths, Action<string> out_)
    {
        foreach (var path in paths.Select(p => p.Path).Order(StringComparer.OrdinalIgnoreCase))
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = HidApi.OpenForFeature(path, out _);
                if (!HidApi.IsValid(handle) || HidWatcher.DescribeHandle(handle, path, 0) is not { } caps
                    || caps.FeatureLength < RazerReport.HidBufferSize)
                {
                    continue;
                }
                var probe = RazerReport.BuildBatteryQuery(0x1F);
                if (QueryRaw(handle, probe, out var kind1) != RazerResponseKind.Success)
                {
                    continue; // not the working channel
                }
                out_($"— combo sweep on …{path[^48..]} —");
                foreach (var tx in new byte[] { 0x1F, 0x9F, 0x3F, 0xFF, 0x0F, 0x2F })
                {
                    var sreq = RazerReport.BuildSerialQuery(tx);
                    var sKind = QueryRaw(handle, sreq, out var sRecv);
                    var serial = sKind == RazerResponseKind.Success && RazerReport.TryGetSerial(sRecv, tx, out var s)
                        ? s
                        : null;
                    out_($"  tx=0x{tx:X2} serial: {sKind}{(serial is { } ? $" \"{serial}\"" : "")}");
                    var breq = RazerReport.BuildBatteryQuery(tx);
                    var bKind = QueryRaw(handle, breq, out var bRecv);
                    var arg = bKind == RazerResponseKind.Success ? $" raw={bRecv[10]}" : "";
                    out_($"  tx=0x{tx:X2} battery: {bKind}{arg}");
                    var creq = RazerReport.BuildChargingQuery(tx);
                    var cKind = QueryRaw(handle, creq, out var cRecv);
                    var carg = cKind == RazerResponseKind.Success ? $" flag={cRecv[10]}" : "";
                    out_($"  tx=0x{tx:X2} charging: {cKind}{carg}");
                }
                // Device-index addressing: arguments[0] = 0/1 with both ids.
                foreach (var tx in new byte[] { 0x1F, 0x9F })
                {
                    foreach (var idx in new byte[] { 0x00, 0x01 })
                    {
                        var q = RazerReport.BuildBatteryQuery(tx);
                        q[9] = idx;
                        q[89] = RazerReport.CalculateCrc(q);
                        var kind = QueryRaw(handle, q, out var recv);
                        var arg = kind == RazerResponseKind.Success ? $" raw={recv[10]}" : "";
                        out_($"  tx=0x{tx:X2} args0=0x{idx:X2} battery: {kind}{arg}");
                    }
                }
                return;
            }
            finally
            {
                if (HidApi.IsValid(handle))
                {
                    HidApi.CloseHandle(handle);
                }
            }
        }
        out_("— combo sweep: no talking collection found —");
    }

    /// <summary>Probe the vendor INFO-class space for a "device name"
    /// command: any response carrying a printable ASCII run >= 4 gets
    /// dumped. Success would give model names without any Synapse trace.</summary>
    private static void NameSweep(List<(string Path, int Pid)> paths, Action<string> out_)
    {
        foreach (var path in paths.Select(p => p.Path).Order(StringComparer.OrdinalIgnoreCase))
        {
            var handle = HidApi.OpenForFeature(path, out _);
            if (!HidApi.IsValid(handle) || HidWatcher.DescribeHandle(handle, path, 0) is not { } caps
                || caps.FeatureLength < RazerReport.HidBufferSize)
            {
                continue;
            }
            if (QueryRaw(handle, RazerReport.BuildBatteryQuery(0x1F), out _) != RazerResponseKind.Success)
            {
                HidApi.CloseHandle(handle);
                continue;
            }
            out_($"— name sweep on …{path[^48..]} —");
            foreach (var tx in new byte[] { 0x9F, 0x1F })
            {
                foreach (var cls in new byte[] { 0x00, 0x04, 0x0F })
                {
                    foreach (var id in new byte[] { 0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A })
                    {
                        var q = RazerReport.BuildQuery(tx, cls, id);
                        var kind = QueryRaw(handle, q, out var recv);
                        if (kind != RazerResponseKind.Success)
                        {
                            continue;
                        }
                        var text = PrintableRun(recv);
                        out_($"  tx=0x{tx:X2} cls=0x{cls:X2} id=0x{id:X2}: " +
                             (text.Length > 0 ? $"\"{text}\"" : Convert.ToHexString(recv, 10, 12)));
                    }
                }
            }
            HidApi.CloseHandle(handle);
            return;
        }
        out_("— name sweep: no talking collection —");
    }

    private static string PrintableRun(byte[] recv)
    {
        var chars = new List<char>();
        for (int i = 9; i < 89; i++)
        {
            chars.Add(recv[i] >= 0x20 && recv[i] <= 0x7e ? (char)recv[i] : ' ');
        }
        var text = new string(chars.ToArray()).Trim();
        // Longest printable run.
        var best = "";
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length > best.Length)
            {
                best = part;
            }
        }
        return best.Length >= 4 ? best : "";
    }

    // Patient exchange for sweeps: the shared dongle's radio gets busy (the
    // paired keyboard streams), so single-shot probes drown in NoResponse.
    private static RazerResponseKind QueryRaw(IntPtr handle, byte[] query, out byte[] recv, int attempts = 4, int waitMs = 120)
    {
        recv = new byte[RazerReport.HidBufferSize];
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (!HidApi.HidD_SetFeature(handle, query, (uint)query.Length))
            {
                return RazerResponseKind.BadLength; // reuse as "API failure"
            }
            Thread.Sleep(waitMs);
            if (HidApi.HidD_GetFeature(handle, recv, (uint)recv.Length))
            {
                var kind = RazerReport.ParseResponse(recv, query[2], query[7], query[8], out _);
                if (kind is RazerResponseKind.Success or RazerResponseKind.NotSupported)
                {
                    return kind;
                }
            }
            Thread.Sleep(150 * (attempt + 1));
        }
        return RazerResponseKind.NoResponse;
    }

    /// <summary>Peek the current input report of every collection, then
    /// stream each for a few seconds — battery/telemetry frames pushed by a
    /// paired keyboard would show up here (keyboard collections stream only
    /// while keys are pressed).</summary>
    private static void SniffInputReports(List<(string Path, int Pid)> paths, Action<string> out_)
    {
        out_("— input report sniff (6s) —");
        var sniffers = new List<(string Label, IntPtr Handle, byte[] Buffer, List<string> Log)>();
        foreach (var (path, pid) in paths)
        {
            var handle = HidApi.OpenForFeature(path, out var access);
            if (!HidApi.IsValid(handle))
            {
                continue;
            }
            if (HidWatcher.DescribeHandle(handle, path, pid) is not { } caps || caps.InputLength < 2)
            {
                HidApi.CloseHandle(handle);
                continue;
            }
            var peek = new byte[caps.InputLength];
            if (HidApi.HidD_GetInputReport(handle, peek, (uint)peek.Length))
            {
                out_($"  peek …{path[^36..]} in={caps.InputLength}: {Hex(peek)}");
            }
            sniffers.Add((path[^36..], handle, new byte[caps.InputLength], new List<string>()));
        }
        if (sniffers.Count == 0)
        {
            out_("  nothing to sniff");
            return;
        }
        var stop = DateTime.UtcNow.AddSeconds(6);
        var threads = sniffers.Select(s => new Thread(() =>
        {
            while (DateTime.UtcNow < stop && s.Log.Count < 24)
            {
                if (!HidApi.ReadFile(s.Handle, s.Buffer, (uint)s.Buffer.Length, out var read, IntPtr.Zero))
                {
                    break; // canceled or device gone
                }
                if (read > 0)
                {
                    lock (s.Log)
                    {
                        var frame = Convert.ToHexString(s.Buffer, 0, (int)read);
                        if (s.Log.Count == 0 || s.Log[^1] != frame)
                        {
                            s.Log.Add(frame);
                        }
                    }
                }
            }
        }) { IsBackground = true }).ToList();
        threads.ForEach(t => t.Start());
        Thread.Sleep(6500);
        foreach (var s in sniffers)
        {
            HidApi.CancelIoEx(s.Handle, IntPtr.Zero);
        }
        threads.ForEach(t => t.Join(2000));
        foreach (var s in sniffers)
        {
            out_($"  stream …{s.Label}: {(s.Log.Count == 0 ? "(silent)" : string.Join(" | ", s.Log.Take(10)))}");
            HidApi.CloseHandle(s.Handle);
        }
    }

    private static void AttachParentConsole()
    {
        try
        {
            if (AttachConsole(0xFFFF_FFFFu))
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            }
        }
        catch
        {
            // Windowed process without a parent console: the log file still
            // carries the output.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);
}

internal static class HidDictionaryExt
{
    public static TValue GetOrAddNew<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key) where TKey : notnull where TValue : new()
        => dict.TryGetValue(key, out var v) ? v : dict[key] = new TValue();
}
