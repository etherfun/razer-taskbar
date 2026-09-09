// Bluetooth-LE battery fallback. The Razer 90-byte vendor feature channel
// does not exist on the BLE HID report map (probed on a Joro, 2026-09: every
// collection rejects the 91-byte buffer with ERROR_INVALID_PARAMETER), so
// the battery comes from the standard GATT Battery Service (0x180F/0x2A19)
// instead — the same source Windows Settings reads. Identity prefers the
// Device Information Service serial number (0x2A25), which matches the
// vendor serial used in dongle/cable mode, keeping one identity across
// transports; without it the BLE MAC stands in. GATT has no charging flag
// (IsCharging stays unknown while on BT).
//
// Reads are uncached GATT round-trips at poll cadence; the device object is
// created and disposed per poll (fine at 15 s, Windows bonds the link).

using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace RazerTaskbar.Core;

public static class BleBattery
{
    public readonly record struct BleReading(byte Percent, string? Serial, string Name);

    // Identity/display are stable per device: cache the serial and name so
    // only the battery level is a live read.
    private static readonly Dictionary<ulong, string?> Serials = new();
    private static readonly Dictionary<ulong, string> Names = new();
    private static long _nextFailLogMs;

    /// <summary>Read the battery level over GATT. Null = device unreachable
    /// or service missing (keep the previous state via the miss counter).</summary>
    public static BleReading? TryRead(ulong address)
    {
        try
        {
            var device = BluetoothLEDevice.FromBluetoothAddressAsync(address)
                .AsTask().GetAwaiter().GetResult();
            if (device is null)
            {
                FailLog($"BLE device {address:X12} not reachable");
                return null;
            }
            using (device)
            {
                if (ReadLevel(device) is not { } percent)
                {
                    FailLog($"BLE {address:X12}: battery service unreadable");
                    return null;
                }
                if (!Serials.TryGetValue(address, out var serial))
                {
                    serial = ReadString(device, GattServiceUuids.DeviceInformation,
                        GattCharacteristicUuids.SerialNumberString);
                    serial = serial is { Length: >= 6 } ? serial : null;
                    if (serial is not null)
                    {
                        Serials[address] = serial;
                        Log.Info($"ble {address:X12}: serial {serial}");
                    }
                    // else: leave uncached — a transient DIS read failure
                    // must not root the BLE:{mac} fallback identity for the
                    // device's lifetime (same policy as HidWatcher.SerialFor).
                }
                if (!Names.TryGetValue(address, out var name))
                {
                    name = device.Name ?? "";
                    Names[address] = name;
                }
                return new BleReading(percent, serial, name);
            }
        }
        catch (Exception e)
        {
            FailLog($"BLE {address:X12}: {e.Message}");
            return null;
        }
    }

    private static byte? ReadLevel(BluetoothLEDevice device)
        => ReadByte(device, GattServiceUuids.Battery, GattCharacteristicUuids.BatteryLevel);

    /// <summary>Diagnostic: enumerate every service and characteristic
    /// (probe support; read-only). Readable values are read; the rest only
    /// report their properties. Returns (service uuid, characteristic uuid,
    /// properties, value bytes or null).</summary>
    public static List<(string Service, string Characteristic, string Props, byte[]? Value)> DumpCharacteristics(ulong address)
    {
        var dump = new List<(string, string, string, byte[]?)>();
        try
        {
            var device = BluetoothLEDevice.FromBluetoothAddressAsync(address)
                .AsTask().GetAwaiter().GetResult();
            if (device is null)
            {
                return dump;
            }
            using (device)
            {
                var services = device.GetGattServicesAsync(BluetoothCacheMode.Uncached)
                    .AsTask().GetAwaiter().GetResult();
                if (services.Status != GattCommunicationStatus.Success)
                {
                    // The peripheral (or another stack component, e.g. the HID
                    // host) may refuse attribute discovery — fall back to the
                    // Windows GATT cache, which records services and values
                    // from earlier sessions.
                    services = device.GetGattServicesAsync(BluetoothCacheMode.Cached)
                        .AsTask().GetAwaiter().GetResult();
                    if (services.Status != GattCommunicationStatus.Success)
                    {
                        return dump;
                    }
                }
                foreach (var service in services.Services)
                {
                    using (service)
                    {
                        var characteristics = service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached)
                            .AsTask().GetAwaiter().GetResult();
                        if (characteristics.Status != GattCommunicationStatus.Success)
                        {
                            // AccessDenied usually means another component (the
                            // HID host, or Synapse) holds the service with an
                            // exclusive session — a shared-open re-try lets
                            // multiple readers coexist.
                            var open = service.OpenAsync(GattSharingMode.SharedReadAndWrite)
                                .AsTask().GetAwaiter().GetResult();
                            if (open == GattOpenStatus.Success)
                            {
                                characteristics = service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached)
                                    .AsTask().GetAwaiter().GetResult();
                            }
                        }
                        if (characteristics.Status != GattCommunicationStatus.Success)
                        {
                            characteristics = service.GetCharacteristicsAsync(BluetoothCacheMode.Cached)
                                .AsTask().GetAwaiter().GetResult();
                        }
                        if (characteristics.Status != GattCommunicationStatus.Success)
                        {
                            dump.Add((service.Uuid.ToString(), $"<chars {characteristics.Status}>", "", null));
                            continue;
                        }
                        foreach (var characteristic in characteristics.Characteristics)
                        {
                            var props = characteristic.CharacteristicProperties.ToString();
                            byte[]? bytes = null;
                            if ((characteristic.CharacteristicProperties & GattCharacteristicProperties.Read) != 0)
                            {
                                var result = characteristic.ReadValueAsync(BluetoothCacheMode.Uncached)
                                    .AsTask().GetAwaiter().GetResult();
                                if (result.Status != GattCommunicationStatus.Success)
                                {
                                    result = characteristic.ReadValueAsync(BluetoothCacheMode.Cached)
                                        .AsTask().GetAwaiter().GetResult();
                                }
                                if (result.Status == GattCommunicationStatus.Success && result.Value.Length > 0)
                                {
                                    bytes = new byte[result.Value.Length];
                                    DataReader.FromBuffer(result.Value).ReadBytes(bytes);
                                }
                            }
                            dump.Add((service.Uuid.ToString(), characteristic.Uuid.ToString(), props, bytes));
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log.Error($"ble dump failed ({address:X12})", e);
        }
        return dump;
    }

    /// <summary>Longest printable-ASCII run of a GATT value ("" when none).</summary>
    public static string Printable(byte[] value)
    {
        var best = "";
        foreach (var part in Encoding.UTF8.GetString(value)
                     .Split(new[] { '\0', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length > best.Length && part.All(c => c is >= '\x20' and <= '\x7e'))
            {
                best = part;
            }
        }
        return best.Length >= 3 ? best : "";
    }

    private static byte? ReadByte(BluetoothLEDevice device, Guid serviceUuid, Guid characteristicUuid)
    {
        using var service = OpenService(device, serviceUuid);
        if (service is null)
        {
            return null;
        }
        var characteristics = service.GetCharacteristicsForUuidAsync(characteristicUuid)
            .AsTask().GetAwaiter().GetResult();
        if (characteristics.Status != GattCommunicationStatus.Success
            || characteristics.Characteristics.Count == 0)
        {
            return null;
        }
        var result = characteristics.Characteristics[0]
            .ReadValueAsync(BluetoothCacheMode.Uncached).AsTask().GetAwaiter().GetResult();
        if (result.Status != GattCommunicationStatus.Success || result.Value.Length == 0)
        {
            return null;
        }
        return DataReader.FromBuffer(result.Value).ReadByte();
    }

    private static string? ReadString(BluetoothLEDevice device, Guid serviceUuid, Guid characteristicUuid)
    {
        using var service = OpenService(device, serviceUuid);
        if (service is null)
        {
            return null;
        }
        var characteristics = service.GetCharacteristicsForUuidAsync(characteristicUuid)
            .AsTask().GetAwaiter().GetResult();
        if (characteristics.Status != GattCommunicationStatus.Success
            || characteristics.Characteristics.Count == 0)
        {
            return null;
        }
        var result = characteristics.Characteristics[0]
            .ReadValueAsync(BluetoothCacheMode.Uncached).AsTask().GetAwaiter().GetResult();
        if (result.Status != GattCommunicationStatus.Success || result.Value.Length == 0)
        {
            return null;
        }
        var bytes = new byte[result.Value.Length];
        DataReader.FromBuffer(result.Value).ReadBytes(bytes);
        // Printable check: garbage serials are worse than none (see RazerReport.TryGetSerial).
        return bytes.All(b => b is >= 0x20 and <= 0x7e) ? Encoding.UTF8.GetString(bytes) : null;
    }

    private static GattDeviceService? OpenService(BluetoothLEDevice device, Guid serviceUuid)
    {
        var services = device.GetGattServicesForUuidAsync(serviceUuid, BluetoothCacheMode.Uncached)
            .AsTask().GetAwaiter().GetResult();
        return services.Status == GattCommunicationStatus.Success && services.Services.Count > 0
            ? services.Services[0]
            : null;
    }

    private static void FailLog(string message)
    {
        long now = Environment.TickCount64;
        if (now < _nextFailLogMs)
        {
            return;
        }
        _nextFailLogMs = now + 60_000;
        Log.Info("ble: " + message);
    }
}
