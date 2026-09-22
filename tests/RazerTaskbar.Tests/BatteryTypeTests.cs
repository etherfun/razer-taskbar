// Battery type: openrazer decides how a device is powered by MODEL — its
// charge-status switch short-circuits a hard-coded AA/AAA PID list to "0"
// (razermouse_driver.c) — so detection here is a name table too, on top of the
// per-device override the history page writes.

using RazerTaskbar.Core;
using Xunit;

namespace RazerTaskbar.Tests;

public sealed class BatteryTypeTests
{
    [Theory]
    [InlineData("Razer Atheris")]
    [InlineData("Razer Atheris (Receiver)")]
    [InlineData("Razer Orochi 2013")]
    [InlineData("Razer Orochi V2")]
    [InlineData("Razer Orochi V2 (Bluetooth)")]
    [InlineData("Razer Basilisk X HyperSpeed")]
    [InlineData("Razer Basilisk V3 X HyperSpeed")]
    [InlineData("Razer Basilisk Mobile (Receiver)")]
    [InlineData("Razer Basilisk Mobile (Wired)")]
    [InlineData("Razer DeathAdder V2 X HyperSpeed")]
    [InlineData("Razer Naga V2 HyperSpeed (Receiver)")]
    [InlineData("Razer Viper V3 HyperSpeed")]
    [InlineData("Razer Pro Click Mini (Receiver)")]
    [InlineData("razer viper v3 hyperspeed")] // name matching is case-insensitive
    public void DetectsReplaceableModels(string name)
        => Assert.Equal(BatteryType.Replaceable, BatteryTypes.Detect(name));

    [Theory]
    // The rechargeable siblings of the AA/AAA family.
    [InlineData("Razer Viper V3 Pro (Wireless)")]
    [InlineData("Razer Naga V2 Pro (Wireless)")]
    [InlineData("Razer Basilisk V3 Pro 35K (Wireless)")]
    [InlineData("Razer DeathAdder V3 Pro (Wireless)")]
    [InlineData("Razer Basilisk Ultimate (Receiver)")]
    // HyperSpeed KEYBOARDS run a built-in pack: a bare "hyperspeed" suffix
    // must not classify them as replaceable.
    [InlineData("Razer BlackWidow V3 Mini HyperSpeed (Wireless)")]
    [InlineData("Razer DeathStalker V2 Pro TKL (Wireless)")]
    // Unknown and missing names fall back to the rechargeable default.
    [InlineData("Razer Kraken Kitty Edition")]
    [InlineData("")]
    [InlineData(null)]
    public void RechargeableByDefault(string? name)
        => Assert.Equal(BatteryType.Rechargeable, BatteryTypes.Detect(name));

    [Fact]
    public void ExplicitSettingWinsOverModelTable()
    {
        // The user can be right where the table is not: a pack misreads as an
        // AA model (or the reverse) must be overridable both ways.
        Assert.Equal(BatteryType.Rechargeable,
            BatteryTypes.Resolve(BatteryType.Rechargeable, "Razer Orochi V2"));
        Assert.Equal(BatteryType.Replaceable,
            BatteryTypes.Resolve(BatteryType.Replaceable, "Razer Viper V3 Pro"));
        // Auto defers to the table.
        Assert.Equal(BatteryType.Replaceable,
            BatteryTypes.Resolve(BatteryType.Auto, "Razer Orochi V2"));
        Assert.Equal(BatteryType.Rechargeable,
            BatteryTypes.Resolve(BatteryType.Auto, "Razer Viper V3 Pro"));
    }

    [Fact]
    public void ConfigSpellingRoundTrips()
    {
        foreach (var t in new[] { BatteryType.Auto, BatteryType.Rechargeable, BatteryType.Replaceable })
        {
            Assert.Equal(t, BatteryTypes.Parse(t.ToConfig()));
        }
        Assert.Equal("auto", BatteryType.Auto.ToConfig());
        Assert.Equal("rechargeable", BatteryType.Rechargeable.ToConfig());
        Assert.Equal("replaceable", BatteryType.Replaceable.ToConfig());
        // Anything unreadable (missing key, blank, typo) means auto, so a
        // hand-edited file degrades to detection instead of half-applying.
        Assert.Equal(BatteryType.Auto, BatteryTypes.Parse(null));
        Assert.Equal(BatteryType.Auto, BatteryTypes.Parse("  "));
        Assert.Equal(BatteryType.Auto, BatteryTypes.Parse("bogus"));
        Assert.Equal(BatteryType.Replaceable, BatteryTypes.Parse(" REPLACEABLE "));
    }

    [Fact]
    public void AsReplaceableDropsTheChargeFlag()
    {
        List<Sample> samples =
        [
            new(0, 90, false, true),
            new(900, 95, true, true), // noise: a cell cannot be charging
            new(1800, 100, true, true),
        ];
        var view = BatteryTypes.AsReplaceable(samples);
        Assert.Equal(3, view.Count);
        Assert.All(view, s => Assert.False(s.Charging));
        // Nothing else is touched — timestamps, levels, connectivity survive.
        Assert.Equal(
            samples.Select(s => (s.Ts, s.Level, s.Connected)),
            view.Select(s => (s.Ts, s.Level, s.Connected)));
    }
}
