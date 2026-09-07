// Display-mode strategies on top of the plain pick: fixed passthrough,
// drop_swap (temporary swap while any other device loses battery), and
// rotate (timed carousel over connected devices, name-ordered).

using RazerTaskbar.Core;
using Xunit;

namespace RazerTaskbar.Tests;

public sealed class DisplayModeTests
{
    private static RazerDevice Dev(string handle, int level, bool charging = false, bool selected = true, bool connected = true)
        => new(handle, handle, level, charging, BatterySaver: false, IsConnected: connected, IsSelected: selected, Kind: DeviceKind.Other);

    private static Config Cfg(string mode, ulong swapSecs = 30, ulong rotSecs = 30) => new()
    {
        DisplayMode = mode,
        SwapDisplaySecs = swapSecs,
        RotateIntervalSecs = rotSecs,
    };

    private static Dictionary<string, RazerDevice> Map(params RazerDevice[] devices)
        => devices.ToDictionary(d => d.Handle);

    // ——— fixed ———

    [Fact]
    public void FixedModeMatchesPlainPick()
    {
        // Delegates to the stamps-based selector: the lower unselected device
        // must NOT win.
        var st = new DisplayModeState();
        var picked = DisplayModeResolver.Pick(
            Map(Dev("a", 5, selected: false), Dev("b", 90)), Cfg("fixed"), st, 1000);
        Assert.Equal("b", picked!.Handle);
        Assert.Equal("b", st.LastShownHandle);
    }

    // ——— drop_swap ———

    [Fact]
    public void FirstSightingPrimesBaselineWithoutSwap()
    {
        var st = new DisplayModeState();
        var picked = DisplayModeResolver.Pick(
            Map(Dev("a", 90), Dev("b", 100)), Cfg("drop_swap"), st, 1000);
        Assert.Equal("a", picked!.Handle); // a is the (auto) shown device
        Assert.Equal("", st.OverrideHandle);
        Assert.Equal(100, st.LastBattery["b"]);
    }

    [Fact]
    public void DropTriggersSwapAndExpiresBackToBase()
    {
        var st = new DisplayModeState();
        var devices = Map(Dev("a", 90), Dev("b", 100));
        DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 1000);

        devices["b"] = Dev("b", 99); // 100 → 99: a drop counts, however small
        Assert.Equal("b", DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 2000)!.Handle);
        Assert.Equal(32000, st.OverrideUntilMs);

        // Within the window: stays on b without new activity.
        Assert.Equal("b", DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 10000)!.Handle);

        // Past the window: back to the shown device.
        Assert.Equal("a", DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 32001)!.Handle);
        Assert.Equal("", st.OverrideHandle);
    }

    [Fact]
    public void DropDuringWindowRearmsTimer()
    {
        var st = new DisplayModeState();
        var devices = Map(Dev("a", 90), Dev("b", 100));
        DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 1000);

        devices["b"] = Dev("b", 99);
        DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 2000); // until 32000

        devices["b"] = Dev("b", 95); // another drop inside the window re-arms
        Assert.Equal("b", DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 30000)!.Handle);
        Assert.Equal(60000, st.OverrideUntilMs);

        Assert.Equal("b", DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 59000)!.Handle);
        Assert.Equal("a", DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 60001)!.Handle);
    }

    [Fact]
    public void SecondDeviceDropSwitchesOverride()
    {
        // a is fixed; b overrides, then c's fresher drop takes over.
        var st = new DisplayModeState();
        var devices = Map(Dev("a", 90), Dev("b", 100, selected: false), Dev("c", 80, selected: false));
        DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 1000);

        devices["b"] = Dev("b", 99, selected: false);
        DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 2000);
        Assert.Equal("b", st.OverrideHandle);

        devices["c"] = Dev("c", 79, selected: false);
        Assert.Equal("c", DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 3000)!.Handle);
        Assert.Equal("c", st.OverrideHandle);
    }

    [Fact]
    public void SameTickMultipleDropsPickLowest()
    {
        var st = new DisplayModeState();
        var devices = Map(Dev("a", 90), Dev("b", 60, selected: false), Dev("c", 30, selected: false));
        DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 1000);

        devices["b"] = Dev("b", 50, selected: false);
        devices["c"] = Dev("c", 20, selected: false);
        var picked = DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 2000);
        Assert.Equal("c", picked!.Handle);
    }

    [Fact]
    public void ShownDeviceOwnDropDoesNotSwap()
    {
        var st = new DisplayModeState();
        var devices = Map(Dev("a", 90), Dev("b", 100));
        DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 1000);

        devices["a"] = Dev("a", 80); // a is the auto base; it already IS the news
        Assert.Equal("a", DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 2000)!.Handle);
        Assert.Equal("", st.OverrideHandle);
    }

    [Fact]
    public void BaseDropWhileOverriddenFallsBackToBase()
    {
        // b is overriding; the fixed device a drops — the freshest drop wins.
        var st = new DisplayModeState();
        var devices = Map(Dev("a", 90), Dev("b", 100, selected: false));
        DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 1000);

        devices["b"] = Dev("b", 99, selected: false);
        DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 2000);
        Assert.Equal("b", st.OverrideHandle);

        devices["a"] = Dev("a", 85);
        Assert.Equal("a", DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 3000)!.Handle);
        Assert.Equal("", st.OverrideHandle);
    }

    [Fact]
    public void OverrideDeviceDisconnectReturnsToBase()
    {
        var st = new DisplayModeState();
        var devices = Map(Dev("a", 90), Dev("b", 100));
        DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 1000);

        devices["b"] = Dev("b", 99);
        DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 2000);

        devices["b"] = Dev("b", 99, connected: false);
        Assert.Equal("a", DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 3000)!.Handle);
    }

    [Fact]
    public void ReconnectLowerThanBaselineTriggers()
    {
        // Headphones leave the case at 100, come back seen at 30: the drop
        // (observed across the gap) is exactly the news worth showing.
        var st = new DisplayModeState();
        var devices = Map(Dev("a", 90), Dev("b", 100));
        DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 1000);

        devices["b"] = Dev("b", 100, connected: false);
        DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 2000);

        devices["b"] = Dev("b", 30);
        Assert.Equal("b", DisplayModeResolver.Pick(devices, Cfg("drop_swap"), st, 3000)!.Handle);
    }

    // ——— rotate ———

    [Fact]
    public void RotatesInNameOrderAndWraps()
    {
        var st = new DisplayModeState();
        var devices = Map(Dev("c", 10), Dev("a", 20), Dev("b", 30)); // scrambled insert order
        var cfg = Cfg("rotate", rotSecs: 30);

        Assert.Equal("a", DisplayModeResolver.Pick(devices, cfg, st, 1000)!.Handle); // clock starts
        Assert.Equal("a", DisplayModeResolver.Pick(devices, cfg, st, 1001)!.Handle); // inside interval
        Assert.Equal("b", DisplayModeResolver.Pick(devices, cfg, st, 31000)!.Handle);
        Assert.Equal("c", DisplayModeResolver.Pick(devices, cfg, st, 61000)!.Handle);
        Assert.Equal("a", DisplayModeResolver.Pick(devices, cfg, st, 91000)!.Handle); // wraps
    }

    [Fact]
    public void RotateClampsWhenListShrinks()
    {
        var st = new DisplayModeState();
        var devices = Map(Dev("a", 20), Dev("b", 30), Dev("c", 10));
        var cfg = Cfg("rotate", rotSecs: 30);
        DisplayModeResolver.Pick(devices, cfg, st, 1000);
        DisplayModeResolver.Pick(devices, cfg, st, 31000);
        DisplayModeResolver.Pick(devices, cfg, st, 61000); // index 2 (c)

        devices["c"] = Dev("c", 10, connected: false);
        var picked = DisplayModeResolver.Pick(devices, cfg, st, 62000);
        Assert.Equal("a", picked!.Handle); // 2 % 2 → wraps onto the shrunken list
        Assert.Equal("a", st.LastShownHandle);
    }

    [Fact]
    public void RotateWithoutConnectedDevicesReturnsNull()
    {
        var st = new DisplayModeState();
        var devices = Map(Dev("a", 20, connected: false));
        Assert.Null(DisplayModeResolver.Pick(devices, Cfg("rotate"), st, 1000));
        Assert.Equal("", st.LastShownHandle);
    }
}
