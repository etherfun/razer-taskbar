// Multi-tray (`tray_device_handles`): the pure selection resolver and the
// tooltip text shared by the single legacy icon and the per-device icons. The
// Win32 slot lifecycle itself lives in Native/TrayIcon.cs (exe) and is covered
// by the build plus deploy smoke, not by these tests.

using RazerTaskbar.Core;
using Xunit;

namespace RazerTaskbar.Tests;

[Collection("I18nSequential")]
public sealed class TraySelectionTests
{
    private static RazerDevice Dev(string handle, int level = 50, bool connected = true,
        bool charging = false, string? name = null)
        => new(name ?? handle, handle, level, charging, BatterySaver: false,
            IsConnected: connected, IsSelected: true, Kind: DeviceKind.Mouse);

    private static RazerDevice Mouse(int level, bool connected, bool charging)
        => Dev("h", level, connected, charging, name: "Mouse");

    private static Dictionary<string, RazerDevice> Map(params RazerDevice[] devices)
        => devices.ToDictionary(d => d.Handle);

    [Fact]
    public void EmptySelectionIsTheLegacySentinel()
    {
        var devices = Map(Dev("a"));
        Assert.Empty(TraySelection.Resolve(null, devices));
        Assert.Empty(TraySelection.Resolve(new List<string>(), devices));
        Assert.Empty(TraySelection.Resolve(new[] { "" }, devices));
    }

    [Fact]
    public void UnknownAndDuplicateHandlesAreSkippedAndOrderIsKept()
    {
        var devices = Map(Dev("a"), Dev("b"));
        var resolved = TraySelection.Resolve(new[] { "b", "gone", "a", "b", "" }, devices);
        Assert.Equal(new[] { "b", "a" }, resolved.Select(d => d.Handle));
    }

    [Fact]
    public void AllUnknownOrEmptyStoreIsTheLegacySentinel()
    {
        Assert.Empty(TraySelection.Resolve(new[] { "gone" }, Map(Dev("a"))));
        Assert.Empty(TraySelection.Resolve(new[] { "a" }, new Dictionary<string, RazerDevice>()));
    }

    [Fact]
    public void ResolveReturnsTheStoredRecords()
    {
        var device = Dev("a", level: 42, connected: false);
        var resolved = TraySelection.Resolve(new[] { "a" }, Map(device));
        Assert.Same(device, resolved[0]);
    }

    [Fact]
    public void TooltipConnectedStates()
    {
        I18n.SetSetting(LanguageSetting.En);
        try
        {
            Assert.Equal("Mouse: 53%", TraySelection.Tooltip(Mouse(53, true, false), null));
            Assert.Equal("Mouse: 53% (charging)", TraySelection.Tooltip(Mouse(53, true, true), null));
            // Estimate only rides on a connected device.
            Assert.Equal("Mouse: 53% · ~3h25m left",
                TraySelection.Tooltip(Mouse(53, true, false), "~3h25m left"));
        }
        finally
        {
            I18n.SetSetting(LanguageSetting.Auto);
        }
    }

    [Fact]
    public void TooltipOfflineHidesChargingAndEstimate()
    {
        I18n.SetSetting(LanguageSetting.En);
        try
        {
            // Recorded-but-offline row: a stale charging flag must not claim it
            // is charging, and a stale prediction is not shown.
            Assert.Equal("Mouse: 53% offline",
                TraySelection.Tooltip(Mouse(53, false, charging: true), "~3h25m left"));
        }
        finally
        {
            I18n.SetSetting(LanguageSetting.Auto);
        }
    }

    [Fact]
    public void TooltipMarkersAreLocalized()
    {
        I18n.SetSetting(LanguageSetting.Zh);
        try
        {
            Assert.Equal("Mouse: 53% （充电中）", TraySelection.Tooltip(Mouse(53, true, true), null));
            Assert.Equal("Mouse: 53% 离线", TraySelection.Tooltip(Mouse(53, false, false), null));
        }
        finally
        {
            I18n.SetSetting(LanguageSetting.Auto);
        }
    }
}
