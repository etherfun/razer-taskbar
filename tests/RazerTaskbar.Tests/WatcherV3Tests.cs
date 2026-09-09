// V3 log-parsing tests. The Rust watcher.rs test module covered V4 only;
// these pin the three V3 regexes to the real Synapse 3 log shapes so the
// "V3/V4 正则逐字保留" contract (docs/agent-csharp.md) actually has a guard.

using RazerTaskbar.Core;
using Xunit;

namespace RazerTaskbar.Tests;

public sealed class WatcherV3Tests
{
    private static List<(string Handle, string Name, int Level, bool Charging, bool Connected)> Parse(string log)
        => RazerWatcher.ParseV3Snapshot(log);

    /// <summary>Real event shape: one timestamped INFO line naming the
    /// handler, the device properties on the following lines. DeathAdder
    /// stays loaded; Joro was loaded then removed (last remove wins).</summary>
    private const string TwoDeviceLog =
        "2026-09-09 08:00:00.1234 INFO Razer Synapse _OnDeviceLoaded handler\n" +
        "Name: Razer DeathAdder V3 Pro\n" +
        "Handle: 101\n" +
        "2026-09-09 08:00:01.1234 INFO Razer Synapse _OnDeviceLoaded handler\n" +
        "Name: Razer Joro\n" +
        "Handle: 202\n" +
        "2026-09-09 08:00:02.1234 INFO Razer Synapse _OnDeviceRemoved handler\n" +
        "Name: Razer Joro\n" +
        "Handle: 202\n" +
        "2026-09-09 08:00:03.1234 INFO Razer Synapse _OnBatteryLevelChanged handler\n" +
        "Name: Razer DeathAdder V3 Pro\n" +
        "Handle: 101\n" +
        "level 85 state 1\n" +
        "2026-09-09 08:00:04.1234 INFO Razer Synapse _OnBatteryLevelChanged handler\n" +
        "Name: Razer Joro\n" +
        "Handle: 202\n" +
        "level 40 state 0\n";

    [Fact]
    public void ParsesBatteryEventsAndConnectionState()
    {
        var snapshot = Parse(TwoDeviceLog);
        Assert.Equal(2, snapshot.Count);

        var adder = snapshot.Single(d => d.Handle == "101");
        Assert.Equal("Razer DeathAdder V3 Pro", adder.Name);
        Assert.Equal(85, adder.Level);
        Assert.True(adder.Charging);
        Assert.True(adder.Connected); // loaded, never removed

        var joro = snapshot.Single(d => d.Handle == "202");
        Assert.Equal("Razer Joro", joro.Name);
        Assert.Equal(40, joro.Level);
        Assert.False(joro.Charging);
        Assert.False(joro.Connected); // removed after loaded
    }

    [Fact]
    public void LastBatteryEventPerHandleWins()
    {
        var log = TwoDeviceLog +
            "2026-09-09 08:00:05.1234 INFO Razer Synapse _OnBatteryLevelChanged handler\n" +
            "Name: Razer Joro\n" +
            "Handle: 202\n" +
            "level 39 state 0\n";
        var snapshot = Parse(log);
        var joro = snapshot.Single(d => d.Handle == "202");
        Assert.Equal(39, joro.Level);
        Assert.False(joro.Charging);
    }

    [Fact]
    public void BatteryOnlyDeviceStaysDisconnected()
    {
        // TS parity: a battery event with no load/remove info must not show
        // stale state — missing events count as index -1 on both sides.
        const string log =
            "2026-09-09 08:00:00.1234 INFO Razer Synapse _OnBatteryLevelChanged handler\n" +
            "Name: Razer Basilisk V3\n" +
            "Handle: 303\n" +
            "level 60 state 0\n";
        var snapshot = Parse(log);
        var d = snapshot.Single(x => x.Handle == "303");
        Assert.False(d.Connected);
    }

    [Fact]
    public void LevelClampsToHundred()
    {
        const string log =
            "2026-09-09 08:00:00.1234 INFO Razer Synapse _OnBatteryLevelChanged handler\n" +
            "Name: Razer Joro\n" +
            "Handle: 202\n" +
            "level 255 state 0\n";
        var snapshot = Parse(log);
        Assert.Equal(100, snapshot.Single(d => d.Handle == "202").Level);
    }

    [Fact]
    public void RemovedAfterLoadedIsDisconnectedAndReloadingRestores()
    {
        // Joro removed at :02, loaded again at :03: the later loaded event
        // (larger offset) reconnects the handle even though the battery
        // event came before the reload.
        const string log =
            "2026-09-09 08:00:00.1234 INFO Razer Synapse _OnDeviceLoaded handler\n" +
            "Name: Razer Joro\n" +
            "Handle: 202\n" +
            "2026-09-09 08:00:02.1234 INFO Razer Synapse _OnDeviceRemoved handler\n" +
            "Name: Razer Joro\n" +
            "Handle: 202\n" +
            "2026-09-09 08:00:03.1234 INFO Razer Synapse _OnBatteryLevelChanged handler\n" +
            "Name: Razer Joro\n" +
            "Handle: 202\n" +
            "level 50 state 0\n" +
            "2026-09-09 08:00:04.1234 INFO Razer Synapse _OnDeviceLoaded handler\n" +
            "Name: Razer Joro\n" +
            "Handle: 202\n";
        var snapshot = Parse(log);
        Assert.True(snapshot.Single(d => d.Handle == "202").Connected);
    }
}
