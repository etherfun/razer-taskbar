// Tests for the HID↔log identity bridge (HidWatcher.ResolveIdentity): one
// physical device must not surface twice when the dongle's vendor serial
// query goes unanswered and the Synapse log already lists the same device
// under its real serial.

using RazerTaskbar.Core;
using Xunit;

namespace RazerTaskbar.Tests;

public sealed class DeviceIdentityTests
{
    private const string Serial = "MP09A12E31000D44";
    private const string Fallback = "HID:0088";

    private static readonly HidDeviceReading Reading =
        new(ProductId: 0x0088, ProductName: "Razer Viper V3 HyperSpeed", Serial: "",
            LevelRaw: 155, LevelPercent: 61, IsCharging: false);

    private static Dictionary<string, RazerDevice> Map(params RazerDevice[] devices)
        => devices.ToDictionary(d => d.Handle);

    [Fact]
    public void FallbackMergesIntoSameNameSerialTwin()
    {
        var map = Map(new RazerDevice("Razer Viper V3 HyperSpeed", Serial, 60, false,
            BatterySaver: false, IsConnected: true, IsSelected: true, Kind: DeviceKind.Mouse));
        var (handle, stale) = HidWatcher.ResolveIdentity(map, Fallback, Reading, new HashSet<string>());
        Assert.Equal(Serial, handle);
        Assert.Equal(Fallback, stale);
    }

    [Fact]
    public void FallbackStandsAloneWithoutSynapse()
    {
        // No log source (Synapse not running): the synthesized identity is
        // the only one — merging must not fire.
        var (handle, stale) = HidWatcher.ResolveIdentity(
            new Dictionary<string, RazerDevice>(), Fallback, Reading, new HashSet<string>());
        Assert.Equal(Fallback, handle);
        Assert.Null(stale);
    }

    [Fact]
    public void RealSerialHandleNeverMerges()
    {
        // A resolved serial is the shared identity across both sources; the
        // dictionary dedupes it without any bridge.
        var (handle, stale) = HidWatcher.ResolveIdentity(
            Map(), Serial, Reading, new HashSet<string>());
        Assert.Equal(Serial, handle);
        Assert.Null(stale);
    }

    [Fact]
    public void NameMismatchDoesNotMerge()
    {
        var map = Map(new RazerDevice("Razer Basilisk V3", Serial, 60, false,
            BatterySaver: false, IsConnected: true, IsSelected: true, Kind: DeviceKind.Mouse));
        var (handle, stale) = HidWatcher.ResolveIdentity(map, Fallback, Reading, new HashSet<string>());
        Assert.Equal(Fallback, handle);
        Assert.Null(stale);
    }

    [Fact]
    public void TwinClaimedThisRoundDoesNotMerge()
    {
        // The serial identity was already written by an earlier reading of
        // the same poll (the dongle answered its serial after all): the
        // fallback must not fold into it — that would be a second unit.
        var map = Map(new RazerDevice("Razer Viper V3 HyperSpeed", Serial, 60, false,
            BatterySaver: false, IsConnected: true, IsSelected: true, Kind: DeviceKind.Mouse));
        var (handle, stale) = HidWatcher.ResolveIdentity(
            map, Fallback, Reading, new HashSet<string> { Serial });
        Assert.Equal(Fallback, handle);
        Assert.Null(stale);
    }

    [Fact]
    public void NameMatchIsCaseInsensitive()
    {
        var map = Map(new RazerDevice("RAZER VIPER V3 HYPERSPEED", Serial, 60, false,
            BatterySaver: false, IsConnected: true, IsSelected: true, Kind: DeviceKind.Mouse));
        var (handle, _) = HidWatcher.ResolveIdentity(map, Fallback, Reading, new HashSet<string>());
        Assert.Equal(Serial, handle);
    }

    [Fact]
    public void BleFallbackMergesToo()
    {
        // The BLE MAC identity has the same fallback problem when the
        // Synapse heartbeat bridge is missing.
        var map = Map(new RazerDevice("Razer Joro", Serial, 96, false,
            BatterySaver: false, IsConnected: true, IsSelected: true, Kind: DeviceKind.Keyboard));
        var ble = new HidDeviceReading(0x02CE, "Razer Joro", "", 200, 96, true);
        var (handle, stale) = HidWatcher.ResolveIdentity(
            map, "BLE:001122334455", ble, new HashSet<string>());
        Assert.Equal(Serial, handle);
        Assert.Equal("BLE:001122334455", stale);
    }

    [Fact]
    public void AnotherFallbackIsNotATwin()
    {
        // Two pid-specific fallbacks must not merge into each other.
        var map = Map(new RazerDevice("Razer Viper V3 HyperSpeed", "HID:00B8", 60, false,
            BatterySaver: false, IsConnected: true, IsSelected: true, Kind: DeviceKind.Mouse));
        var (handle, stale) = HidWatcher.ResolveIdentity(map, Fallback, Reading, new HashSet<string>());
        Assert.Equal(Fallback, handle);
        Assert.Null(stale);
    }
}
