// Port of src/watcher.rs #[cfg(test)] tests (V4 parsing rules).

using System.Text.Json;
using RazerTaskbar.Core;
using Xunit;

namespace RazerTaskbar.Tests;

public sealed class WatcherV4Tests
{
    /// <summary>Real shape from `systray_systrayv2*.log` (camelCase keys + nulls).</summary>
    private const string Sample = """[{"serialNumber":"632516H31000044","hasBattery":true,"deviceContainerId":"{9E502CF7-160A-51EA-8250-14BD19EB4A4A}","powerStatus":{"chargingStatus":"NoCharge_BatteryFull","level":82},"name":{"en":"Razer Viper V3 HyperSpeed"},"category":"MOUSE"},{"serialNumber":null,"hasBattery":true,"deviceContainerId":"{9E502CF7-160A-51EA-8250-14BD19EB4A4A}","powerStatus":null,"name":null,"category":null}]""";

    private static List<V4Device> Parse(string json)
        => JsonSerializer.Deserialize<List<V4Device>>(json)!;

    [Fact]
    public void V4CamelCaseFieldsDeserialize()
    {
        var devs = Parse(Sample);
        Assert.Equal(2, devs.Count);
        Assert.True(devs[0].HasBattery);
        Assert.Equal("632516H31000044", devs[0].SerialNumber);
        Assert.Equal(82, devs[0].PowerStatus.Level);
        Assert.Equal("Razer Viper V3 HyperSpeed", devs[0].Name.En);
        Assert.Equal("MOUSE", devs[0].Category);
    }

    [Fact]
    public void V4CategoryDrivesDeviceKind()
    {
        var devs = Parse(Sample);
        Assert.Equal(DeviceKind.Mouse, DeviceClassifier.FromCategoryAndName(devs[0].Category, devs[0].Name.En));
        // Null category + unknown name falls back to Other.
        Assert.Equal(DeviceKind.Other, DeviceClassifier.FromCategoryAndName(devs[1].Category, devs[1].Name.En));
    }

    [Fact]
    public void V4NullsFallBackToDefaults()
    {
        var devs = Parse(Sample);
        Assert.Equal("", devs[1].SerialNumber);
        Assert.Equal(0, devs[1].PowerStatus.Level);
        Assert.Equal("", devs[1].Name.En);
        // Null serial falls back to the container id (TS `??` parity).
        var handle = devs[1].SerialNumber.Length > 0 ? devs[1].SerialNumber : devs[1].DeviceContainerId;
        Assert.Equal("{9E502CF7-160A-51EA-8250-14BD19EB4A4A}", handle);
    }

    [Fact]
    public void V4ChargingFlagMatchesTsStrictEquality()
    {
        foreach (var (status, expected) in new[]
                 {
                     ("Charging", true),
                     ("NoCharge_BatteryFull", false),
                     ("off", false),
                     ("", false),
                 })
        {
            var json = """[{"serialNumber":"S","hasBattery":true,"deviceContainerId":"C","powerStatus":{"chargingStatus":"__STATUS__","level":50},"name":{"en":"N"}}]"""
                .Replace("__STATUS__", status);
            var devs = Parse(json);
            Assert.Equal(expected, devs[0].PowerStatus.ChargingStatus == "Charging");
        }
    }

    [Fact]
    public void LatestV4LogPicksHighestIndex()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"razer-taskbar-test-{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        foreach (var name in new[] { "systray_systrayv2.log", "systray_systrayv21.log", "systray_systrayv24.log" })
        {
            File.WriteAllText(Path.Combine(dir, name), "x");
        }
        var latest = RazerWatcher.LatestV4Log(dir);
        Assert.Equal("systray_systrayv24.log", Path.GetFileName(latest));
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>Real shapes from `systray_systrayv28.log`: a powered-off device STAYS
    /// in the snapshot with `chargingStatus: "off"` and a frozen level.</summary>
    private const string OffSnapshot = """[{"serialNumber":"632516H31000044","hasBattery":true,"deviceContainerId":"{C1}","powerStatus":{"chargingStatus":"NoCharge_BatteryFull","level":72},"name":{"en":"Razer Viper V3 HyperSpeed"},"category":"MOUSE"},{"serialNumber":"SI2522F18701637","hasBattery":true,"deviceContainerId":"{C1}","powerStatus":{"chargingStatus":"off","level":91},"name":{"en":"Razer Joro"},"category":"KEYBOARD"}]""";

    [Fact]
    public void V4PoweredOffDeviceInSnapshotIsDisconnected()
    {
        var devs = Parse(OffSnapshot);
        var ids = new HashSet<string>();
        var off = new HashSet<string>();
        foreach (var d in devs)
        {
            ids.Add(d.SerialNumber);
            if (d.PowerStatus.ChargingStatus == "off")
            {
                off.Add(d.SerialNumber);
            }
        }
        // Viper on, Joro powered off (but still listed).
        Assert.True(V4Rules.IsConnected(ids, off, "632516H31000044"));
        Assert.False(V4Rules.IsConnected(ids, off, "SI2522F18701637"));
        // A device that dropped out of the snapshot entirely stays offline.
        Assert.False(V4Rules.IsConnected(ids, off, "GONE"));
    }

    /// <summary>Transitional snapshot seen on wired (re)plug: the device is
    /// listed with a NULL powerStatus. Must not flash as connected at 0%.</summary>
    [Fact]
    public void V4NullPowerStatusIsDisconnected()
    {
        var json = """[{"serialNumber":"S","hasBattery":true,"deviceContainerId":"C","powerStatus":null,"name":{"en":"Razer Joro"},"category":"KEYBOARD"}]""";
        var devs = Parse(json);
        Assert.Equal(0, devs[0].PowerStatus.Level);
        Assert.Equal("", devs[0].PowerStatus.ChargingStatus);
        var ids = new HashSet<string>();
        var off = new HashSet<string>();
        var d = devs[0];
        ids.Add(d.SerialNumber);
        if (d.PowerStatus.ChargingStatus.Length == 0 || d.PowerStatus.ChargingStatus == "off")
        {
            off.Add(d.SerialNumber);
        }
        Assert.False(V4Rules.IsConnected(ids, off, "S"));
    }
}
