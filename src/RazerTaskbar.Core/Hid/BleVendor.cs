// Razer vendor GATT channel (service 52401523-f97c-7f90-0e7f-6c6f4e36db1c) —
// Razer's private BLE command channel, reverse engineered 2026-09 from HCI
// captures of Synapse 4.0.699 talking to a Joro over BT plus probe replays
// (method + full command table: docs/agent-hid.md).
//
//   command   = 8-byte Write Request to 52401524:
//               [seq] [payload_len] 00 00 [page] [id] [param:2]
//               payload_len 0 = query; sets carry payload_len bytes in a
//               second Write Request right after the command frame.
//   response  = notifications on 52401525, snapshots of a 20-byte register:
//               header [echo_seq] [len] 00 00 00 00 00 [tag] with tag
//               0x02 = ok (len payload bytes follow in register snapshots)
//               and 0x05 = unknown command; snapshots carry payload from
//               offset 0, bytes beyond the fresh payload keep stale
//               register content (typically the serial tail "8701637") —
//               never interpret past the declared length.
//
// Verified on the Joro by differential probing: battery (05,81) is
// Scaled255 (raw·100/255, matches the dongle/cable keyboard slot encoding
// and rose F7→F9 while charging), charging (05,85) flipped 0→1 with the
// cable and back on unplug. When Synapse holds the channel the shared open
// fails and the caller falls back to the plain Battery Service.

using System.Collections.Concurrent;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace RazerTaskbar.Core;

public static class BleVendor
{
    public static readonly Guid ServiceUuid = new("52401523-f97c-7f90-0e7f-6c6f4e36db1c");
    internal static readonly Guid WriteUuid = new("52401524-f97c-7f90-0e7f-6c6f4e36db1c");
    internal static readonly Guid StatusUuid = new("52401525-f97c-7f90-0e7f-6c6f4e36db1c");
    internal static readonly Guid TokenUuid = new("52401526-f97c-7f90-0e7f-6c6f4e36db1c");

    public readonly record struct VendorPower(byte RawBattery, bool? Charging, string Name);

    private static long _nextFailLogMs;

    /// <summary>Scaled255 → percent (the keyboard's own BAS rounds the same
    /// way: raw 247 reports as 97).</summary>
    public static int BatteryPercent(byte raw)
        => (int)Math.Round(raw * 100.0 / 255.0);

    /// <summary>Command frame [seq][payload_len]00 00[page][id][param:2].</summary>
    internal static byte[] BuildCommand(byte seq, byte payloadLen, byte page, byte id, ushort param)
        => new byte[]
        {
            seq, payloadLen, 0x00, 0x00, page, id,
            (byte)(param >> 8), (byte)(param & 0xFF),
        };

    /// <summary>Header = [echo of our seq][len]00 00 00 00 00[tag] with tag
    /// 0x02 = ok / 0x05 = unknown command. Null when the frame does not
    /// look like a header for this seq.</summary>
    internal static (int Len, byte Tag)? ParseHeader(byte[] n, byte seq)
        => n.Length >= 8 && n[0] == seq && n[2] == 0 && n[3] == 0 && n[4] == 0
            && n[5] == 0 && n[6] == 0 && n[7] is 0x02 or 0x05
            ? (n[1], n[7])
            : null;

    /// <summary>Payload reassembly: data snapshots carry the payload from
    /// offset 0; bytes beyond the fresh payload are stale register content.</summary>
    internal static byte[] ReassemblePayload(int total, IEnumerable<byte[]> frames)
    {
        var data = new List<byte>();
        foreach (var n in frames)
        {
            if (data.Count >= total)
            {
                break;
            }
            data.AddRange(n.Take(Math.Min(n.Length, total - data.Count)));
        }
        return data.ToArray();
    }

    /// <summary>Battery + charging straight from the vendor channel while
    /// the device is on BT and nothing else holds the channel (Synapse does
    /// once its device page has been opened). Null = channel unavailable or
    /// answers missing — caller keeps the BAS fallback. Runs on a background
    /// (MTA) thread: WinRT event delivery needs no message pump there.</summary>
    public static VendorPower? TryReadPower(ulong address)
    {
        try
        {
            using var device = BluetoothLEDevice.FromBluetoothAddressAsync(address)
                .AsTask().GetAwaiter().GetResult();
            if (device is null)
            {
                return Miss($"vendor power {address:X12}: device unreachable");
            }
            if (OpenService(device) is not { } service)
            {
                return Miss($"vendor power {address:X12}: channel held (Synapse?)");
            }
            using (service)
            {
                var write = FindChar(service, WriteUuid);
                var status = FindChar(service, StatusUuid);
                if (write is null || status is null)
                {
                    return Miss($"vendor power {address:X12}: characteristics missing");
                }
                var inbox = new ConcurrentQueue<byte[]>();
                var cccd = status.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify)
                    .AsTask().GetAwaiter().GetResult();
                if (cccd != GattCommunicationStatus.Success)
                {
                    return Miss($"vendor power {address:X12}: subscribe {cccd}");
                }
                status.ValueChanged += (_, e) => inbox.Enqueue(BufferBytes(e.CharacteristicValue));

                var (kind, raw) = Transact(write, inbox, seq: 0x00,
                    payloadLen: 0x00, page: 0x05, id: 0x81, param: 0x0001, payload: null, waitMs: 900);
                if (kind != "ok" || raw.Length < 1)
                {
                    return Miss($"vendor power {address:X12}: battery query {kind}");
                }
                bool? charging = null;
                var (kind2, flag) = Transact(write, inbox, seq: 0x01,
                    payloadLen: 0x00, page: 0x05, id: 0x85, param: 0x0001, payload: null, waitMs: 900);
                if (kind2 == "ok" && flag.Length >= 1)
                {
                    charging = flag[0] != 0;
                }
                return new VendorPower(raw[0], charging, device.Name ?? "");
            }
        }
        catch (Exception e)
        {
            return Miss($"vendor power {address:X12}: {e.Message}");
        }
    }

    private static VendorPower? Miss(string message)
    {
        long now = Environment.TickCount64;
        if (now >= _nextFailLogMs)
        {
            _nextFailLogMs = now + 60_000;
            Log.Info(message);
        }
        return null;
    }

    /// <summary>One round-trip: write the command frame (+payload write for
    /// sets), then collect the response — a header notification (echo of the
    /// seq, declared payload length, tag 0x02=ok/0x05=unknown) followed by
    /// data snapshots until the payload is complete. Fully serialized: the
    /// next command goes out only after this response is settled, which also
    /// disambiguates data frames that happen to start with the echo byte.</summary>
    internal static (string Kind, byte[] Payload) Transact(GattCharacteristic write,
        ConcurrentQueue<byte[]> inbox, byte seq, byte payloadLen, byte page, byte id,
        ushort param, byte[]? payload, int waitMs, Action<string>? trace = null)
    {
        var frame = BuildCommand(seq, payloadLen, page, id, param);
        trace?.Invoke($"→ [{seq:X2}] page {page:X2} id {id:X2} param {param:X4}{(payloadLen > 0 ? $" payload={Convert.ToHexString(payload ?? Array.Empty<byte>())}" : "")}");
        while (inbox.TryDequeue(out _))
        {
        }
        if (Send(write, frame) is { } error)
        {
            trace?.Invoke($"   write failed: {error}");
            return ("fail", Array.Empty<byte>());
        }
        if (payload is { Length: > 0 })
        {
            Send(write, payload);
        }

        string kind = "";
        var total = -1;
        var chunks = new List<byte[]>();
        var deadline = Environment.TickCount64 + waitMs;
        while (Environment.TickCount64 < deadline)
        {
            if (!inbox.TryDequeue(out var n))
            {
                Thread.Sleep(15);
                continue;
            }
            if (total < 0)
            {
                if (ParseHeader(n, seq) is { } hdr)
                {
                    (total, var tag) = hdr;
                    kind = tag == 0x05 ? "err" : "ok";
                    trace?.Invoke($"   ← hdr echo={n[0]:X2} len={total} tag=0x{tag:X2}");
                    // Data snapshots trail the header — keep waiting for them.
                    deadline = Math.Min(deadline, Environment.TickCount64 + 900);
                    if (total == 0)
                    {
                        break;
                    }
                }
                else
                {
                    trace?.Invoke($"   stray {Convert.ToHexString(n)}");
                }
                continue;
            }
            chunks.Add(n);
            trace?.Invoke($"   +{Convert.ToHexString(n, 0, Math.Min(n.Length, total))}");
            if (chunks.Sum(c => c.Length) >= total)
            {
                break;
            }
        }
        return (kind, total > 0 ? ReassemblePayload(total, chunks) : Array.Empty<byte>());
    }

    internal static byte[] BufferBytes(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        DataReader.FromBuffer(buffer).ReadBytes(bytes);
        return bytes;
    }

    internal static string? Send(GattCharacteristic write, byte[] data)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(data);
        var result = write.WriteValueAsync(writer.DetachBuffer()).AsTask().GetAwaiter().GetResult();
        return result == GattCommunicationStatus.Success ? null : result.ToString();
    }

    internal static ulong? FindBleMac()
        => HidWatcher.EnumerateRazerInterfacePaths()
            .Select(p => p.Path)
            .Where(HidWatcher.IsBlePath)
            .Select(HidWatcher.BleMac)
            .FirstOrDefault(m => m is not null);

    internal static GattDeviceService? OpenService(BluetoothLEDevice device)
    {
        var services = device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached)
            .AsTask().GetAwaiter().GetResult();
        if (services.Status == GattCommunicationStatus.Success && services.Services.Count > 0)
        {
            return services.Services[0];
        }
        // Another stack component may hold the service — try the shared open.
        var retry = device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached)
            .AsTask().GetAwaiter().GetResult();
        foreach (var s in retry.Services)
        {
            var open = s.OpenAsync(GattSharingMode.SharedReadAndWrite).AsTask().GetAwaiter().GetResult();
            if (open == GattOpenStatus.Success)
            {
                return s;
            }
            s.Dispose();
        }
        return null;
    }

    internal static GattCharacteristic? FindChar(GattDeviceService service, Guid uuid)
    {
        var chars = service.GetCharacteristicsForUuidAsync(uuid).AsTask().GetAwaiter().GetResult();
        return chars.Status == GattCommunicationStatus.Success && chars.Characteristics.Count > 0
            ? chars.Characteristics[0]
            : null;
    }
}

/// <summary>`RazerTaskbar --ble-vendor`: standalone diagnostic for the vendor
/// channel — replays the queries observed in the Synapse HCI capture,
/// sweeps the read-only get-half id space, and accepts single raw commands.
/// Needs Synapse stopped (it holds the channel once its device page has
/// been opened); refuses writes unless explicitly overridden.</summary>
public static class BleVendorProbe
{
    /// <summary>Query commands observed in the Synapse HCI capture, exact
    /// bytes (page, id, param) — the replay baseline.</summary>
    private static readonly (byte Page, byte Id, ushort Param)[] ObservedQueries =
    {
        (0x01, 0x86, 0x0000), (0x01, 0x83, 0x0000), (0x01, 0x82, 0x0000),
        (0x01, 0xA0, 0x0000),
        (0x05, 0x80, 0x0001), (0x05, 0x81, 0x0001), (0x05, 0x84, 0x0000),
        (0x05, 0x85, 0x0001), (0x05, 0x87, 0x0001), (0x05, 0x8A, 0x0001),
        (0x05, 0x8D, 0x0001),
    };

    private static Action<string> _out = static s => Console.WriteLine(s);
    private static readonly ConcurrentQueue<byte[]> Notifications = new();

    public static int Run(string[] args)
    {
        // The probe runs on an MTA thread-pool thread: the WinRT
        // ValueChanged callbacks are dispatched without a message pump, and
        // blocking the STA entry thread starves them (notifications arrive
        // seconds late and break response framing).
        return Task.Run(() => ProbeCore(args)).GetAwaiter().GetResult();
    }

    private static int ProbeCore(string[] args)
    {
        var lines = new List<string>();
        var dir = Path.Combine(
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPDATA")) ? "." : Environment.GetEnvironmentVariable("APPDATA")!,
            "razer-taskbar");
        Directory.CreateDirectory(dir);
        var logPath = Path.Combine(dir, "ble-vendor.log");
        _out = s =>
        {
            lines.Add(s);
            Console.WriteLine(s);
        };

        _out($"ble-vendor probe {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        ulong? mac = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--mac=", StringComparison.OrdinalIgnoreCase)
                && ulong.TryParse(arg[6..], System.Globalization.NumberStyles.HexNumber, null, out var parsed))
            {
                mac = parsed;
            }
        }
        mac ??= BleVendor.FindBleMac();
        if (mac is null)
        {
            _out("no BLE Razer device found (pass --mac=XXXXXXXXXXXX)");
            File.WriteAllLines(logPath, lines);
            return 1;
        }
        _out($"target BLE {mac.Value:X12}");

        if (args.Contains("--power"))
        {
            var power = BleVendor.TryReadPower(mac.Value);
            _out($"TryReadPower: {(power is { } p
                ? $"raw=0x{p.RawBattery:X2} ({BleVendor.BatteryPercent(p.RawBattery)}%) charging={p.Charging?.ToString() ?? "unknown"} name=\"{p.Name}\""
                : "null (channel held / device unreachable)")}");
            File.WriteAllLines(logPath, lines);
            Console.WriteLine($"[written to {logPath}]");
            return 0;
        }

        using var device = BluetoothLEDevice.FromBluetoothAddressAsync(mac.Value)
            .AsTask().GetAwaiter().GetResult();
        if (device is null)
        {
            _out("device unreachable (not connected / out of range?)");
            File.WriteAllLines(logPath, lines);
            return 1;
        }
        _out($"name: \"{device.Name}\"");

        if (BleVendor.OpenService(device) is not { } service)
        {
            _out("vendor service 52401523… not reachable (Synapse running? stop it first)");
            File.WriteAllLines(logPath, lines);
            return 1;
        }
        using (service)
        {
            var write = BleVendor.FindChar(service, BleVendor.WriteUuid);
            var status = BleVendor.FindChar(service, BleVendor.StatusUuid);
            var token = BleVendor.FindChar(service, BleVendor.TokenUuid);
            _out($"write 52401524: {(write is null ? "MISSING" : "ok")}, status 52401525: {(status is null ? "MISSING" : "ok")}, token 52401526: {(token is null ? "MISSING" : "ok")}");
            if (write is null || status is null)
            {
                File.WriteAllLines(logPath, lines);
                return 1;
            }

            // Read the static values first for context.
            foreach (var (label, ch) in new[] { ("status", status), ("token", token) })
            {
                if (ch is null)
                {
                    continue;
                }
                var read = ch.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask().GetAwaiter().GetResult();
                var value = read.Status == GattCommunicationStatus.Success && read.Value is { } buf ? BleVendor.BufferBytes(buf) : null;
                _out($"read {label}: {(value is { Length: > 0 } bytes
                    ? Convert.ToHexString(bytes) + $" \"{BleBattery.Printable(bytes)}\""
                    : read.Status.ToString())}");
            }

            var cccd = status.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask().GetAwaiter().GetResult();
            _out($"subscribe 52401525: {cccd}");
            status.ValueChanged += (_, e) => Notifications.Enqueue(BleVendor.BufferBytes(e.CharacteristicValue));

            byte seq = 0;
            foreach (var arg in args)
            {
                if (arg.StartsWith("--raw=", StringComparison.OrdinalIgnoreCase))
                {
                    SendRaw(arg[6..], write, ref seq);
                    continue;
                }
                if (arg == "--sweep")
                {
                    Sweep(write, ref seq);
                    continue;
                }
            }
            if (!args.Any(a => a.StartsWith("--raw=", StringComparison.OrdinalIgnoreCase) || a == "--sweep"))
            {
                _out("— replay of observed queries —");
                foreach (var (page, id, param) in ObservedQueries)
                {
                    Exchange(write, ref seq, payloadLen: 0x00, page, id, param, waitMs: 900);
                }
            }

            _out("done");
        }
        File.WriteAllLines(logPath, lines);
        Console.WriteLine($"[written to {logPath}]");
        return 0;
    }

    /// <summary>Traced wrapper around the shared transaction engine.</summary>
    private static (string Kind, byte[] Payload) Exchange(GattCharacteristic write, ref byte seq,
        byte payloadLen, byte page, byte id, ushort param, int waitMs, byte[]? payload = null,
        bool quiet = false)
        => BleVendor.Transact(write, Notifications, seq++, payloadLen, page, id, param,
            payload, waitMs, quiet ? null : s => _out(s));

    /// <summary>Raw mode: --raw=LEN:PAGE:ID:PARAM[:HHHH…] — LEN is the
    /// payload byte count (0 = query); hex payload bytes must match LEN.
    /// Set-half ids and nonzero LEN are gated behind --yes-i-know.</summary>
    private static void SendRaw(string spec, GattCharacteristic write, ref byte seq)
    {
        var parts = spec.Split(':');
        if (parts.Length < 4)
        {
            _out($"bad --raw spec \"{spec}\" (LEN:PAGE:ID:PARAM)");
            return;
        }
        try
        {
            var len = byte.Parse(parts[0], System.Globalization.NumberStyles.HexNumber);
            var page = byte.Parse(parts[1], System.Globalization.NumberStyles.HexNumber);
            var id = byte.Parse(parts[2], System.Globalization.NumberStyles.HexNumber);
            var param = ushort.Parse(parts[3], System.Globalization.NumberStyles.HexNumber);
            byte[]? payload = null;
            if (len > 0)
            {
                payload = Convert.FromHexString(string.Concat(parts[4..]));
                if (payload.Length != len)
                {
                    _out($"bad --raw spec: LEN {len} ≠ payload length {payload.Length}");
                    return;
                }
            }
            if (id < 0x80)
            {
                _out($"refused: id {id:X2} is set-half (docs/agent-hid.md danger list); append --yes-i-know to override");
                return;
            }
            if (len > 0 && !Environment.CommandLine.Contains("--yes-i-know"))
            {
                _out("refused: LEN≠0 is a write; append --yes-i-know to override");
                return;
            }
            Exchange(write, ref seq, len, page, id, param, waitMs: 900, payload);
        }
        catch (FormatException)
        {
            _out($"bad --raw spec \"{spec}\" (hex parse)");
        }
    }

    /// <summary>Read-only get-half sweep: every page/id in the observed
    /// pages, ids 0x80-0xFF, zero-argument query shape. "err" (tag 0x05) is
    /// the device's unknown-command reply — only "ok" (0x02) counts.</summary>
    private static void Sweep(GattCharacteristic write, ref byte seq)
    {
        foreach (var page in new byte[] { 0x01, 0x05 })
        {
            _out($"— sweep page {page:X2} (ids 0x80-0xFF, param 0000) —");
            for (int id = 0x80; id <= 0xFF; id++)
            {
                var (kind, data) = Exchange(write, ref seq, payloadLen: 0x00, page, (byte)id,
                    0x0000, waitMs: 900, quiet: true);
                _out($"   page {page:X2} id {id:X2}: {(kind == "" ? "no response" : kind == "ok" ? $"ok {Convert.ToHexString(data)}" : "err")}");
            }
        }
    }
}
