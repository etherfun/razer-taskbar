// Port of src/battery.rs #[cfg(test)] tests (glyphs + selection + kind).

using RazerTaskbar.Core;
using Xunit;

namespace RazerTaskbar.Tests;

public sealed class BatteryTests
{
    private static RazerDevice Dev(string handle, int level, bool charging, bool selected)
        => new(handle, handle, level, charging, BatterySaver: false, IsConnected: true, IsSelected: selected, Kind: DeviceKind.Other);

    [Fact]
    public void BatteryGlyphSeriesMapping()
    {
        // Normal: E850-E859 + E83F, one glyph per 10% (E83F is truly full).
        Assert.Equal('\uE850', BatteryGlyphs.BatteryGlyph(0, BatteryGlyphState.Normal));
        Assert.Equal('\uE857', BatteryGlyphs.BatteryGlyph(72, BatteryGlyphState.Normal));
        Assert.Equal('\uE83F', BatteryGlyphs.BatteryGlyph(100, BatteryGlyphState.Normal));
        // Charging / saver: 11-glyph series, the tail steps sit at detached
        // codepoints (Charging9/10 = E83E/EA93, Saver9/10 = EA94/EA95).
        Assert.Equal('\uE85A', BatteryGlyphs.BatteryGlyph(0, BatteryGlyphState.Charging));
        Assert.Equal('\uE861', BatteryGlyphs.BatteryGlyph(72, BatteryGlyphState.Charging));
        Assert.Equal('\uE83E', BatteryGlyphs.BatteryGlyph(90, BatteryGlyphState.Charging));
        Assert.Equal('\uEA93', BatteryGlyphs.BatteryGlyph(100, BatteryGlyphState.Charging));
        Assert.Equal('\uE866', BatteryGlyphs.BatteryGlyph(30, BatteryGlyphState.Saver));
        Assert.Equal('\uEA94', BatteryGlyphs.BatteryGlyph(90, BatteryGlyphState.Saver));
        Assert.Equal('\uEA95', BatteryGlyphs.BatteryGlyph(100, BatteryGlyphState.Saver));
        // Normal stays contiguous through 90%: E859.
        Assert.Equal('\uE859', BatteryGlyphs.BatteryGlyph(90, BatteryGlyphState.Normal));
        // Rounding: 0-4% stays on the empty glyph, 5% rounds up a step.
        Assert.Equal('\uE850', BatteryGlyphs.BatteryGlyph(4, BatteryGlyphState.Normal));
        Assert.Equal('\uE851', BatteryGlyphs.BatteryGlyph(5, BatteryGlyphState.Normal));
    }

    [Fact]
    public void StatusOverlayGlyphPicksZeroFillStatusGlyph()
    {
        // Two-layer icon: the overlay is the 0% glyph of layer 1's series
        // (outline + symbol, empty fill) that masks the tinted outline.
        // Disconnected rows draw the gray level glyph only.
        Assert.Null(BatteryGlyphs.StatusOverlayGlyph(false, false, false));
        Assert.Null(BatteryGlyphs.StatusOverlayGlyph(false, true, true));
        // Plain discharge overlays the plain E850 outline (otherwise a high
        // charge would read as a solid colored blob); charging: E85A (bolt);
        // saver wins over charging: E863 (leaf).
        Assert.Equal('\uE850', BatteryGlyphs.StatusOverlayGlyph(true, false, false));
        Assert.Equal('\uE85A', BatteryGlyphs.StatusOverlayGlyph(true, false, true));
        Assert.Equal('\uE863', BatteryGlyphs.StatusOverlayGlyph(true, true, false));
        Assert.Equal('\uE863', BatteryGlyphs.StatusOverlayGlyph(true, true, true));
    }

    [Fact]
    public void LevelGlyphUsesActiveSeries()
    {
        // Layer 1 must come from the same series as the status overlay: the
        // status glyphs cut their outline where the bolt/leaf crosses it,
        // and a normal-series outline underneath would peek through.
        Assert.Equal('\uE855', BatteryGlyphs.LevelGlyph(50, true, false, false));
        Assert.Equal('\uE855', BatteryGlyphs.LevelGlyph(50, false, true, true));
        // Charging 50% = E85F, saver 50% = E868; saver wins over charging.
        Assert.Equal('\uE85F', BatteryGlyphs.LevelGlyph(50, true, false, true));
        Assert.Equal('\uE868', BatteryGlyphs.LevelGlyph(50, true, true, true));
    }

    [Fact]
    public void LevelFillColorUsesStateColors()
    {
        // State colors are independent of the rainbow option: charging is
        // flat #9FD89F regardless of level, saver is #EAA300 and wins over
        // charging, disconnected is gray. Only the plain-discharge level
        // tint is gated by the option: off draws the default white glyph,
        // on keeps the Win11 level tint.
        Assert.Equal(new RgbColor(0x9F, 0xD8, 0x9F), BatteryColors.LevelFillColor(50, true, false, true, colorize: false));
        Assert.Equal(new RgbColor(0x9F, 0xD8, 0x9F), BatteryColors.LevelFillColor(90, true, false, true, colorize: true));
        Assert.Equal(new RgbColor(0xEA, 0xA3, 0x00), BatteryColors.LevelFillColor(50, true, true, true, colorize: false));
        Assert.Equal(new RgbColor(0x80, 0x80, 0x80), BatteryColors.LevelFillColor(50, false, false, true, colorize: false));
        Assert.Equal(new RgbColor(0xFF, 0xFF, 0xFF), BatteryColors.LevelFillColor(72, true, false, false, colorize: false));
        Assert.Equal(BatteryColors.ColorFor(72), BatteryColors.LevelFillColor(72, true, false, false, colorize: true));
    }

    [Fact]
    public void PrefersNonChargingOverLowerCharging()
    {
        var m = new Dictionary<string, RazerDevice>
        {
            ["a"] = Dev("a", 90, false, true),
            ["b"] = Dev("b", 10, true, true),
        };
        Assert.Equal("a", DeviceSelector.PickDeviceToDisplay(m)!.Handle);
    }

    [Fact]
    public void RespectsSelection()
    {
        var m = new Dictionary<string, RazerDevice>
        {
            ["a"] = Dev("a", 5, false, false),
            ["b"] = Dev("b", 90, false, true),
        };
        Assert.Equal("b", DeviceSelector.PickDeviceToDisplay(m)!.Handle);
    }

    [Fact]
    public void KindFromV4CategoryBeatsName()
    {
        // A device named like a mouse but categorized as keyboard stays keyboard.
        Assert.Equal(DeviceKind.Keyboard, DeviceClassifier.FromCategoryAndName("KEYBOARD", "Razer Viper"));
        Assert.Equal(DeviceKind.Mouse, DeviceClassifier.FromCategoryAndName("MOUSE", "Razer Viper V3 HyperSpeed"));
        Assert.Equal(DeviceKind.Headset, DeviceClassifier.FromCategoryAndName("headset", "Razer Thing"));
    }

    [Fact]
    public void KindFallsBackToNameKeywords()
    {
        // V3 logs carry no category: product-line keywords decide.
        Assert.Equal(DeviceKind.Headset, DeviceClassifier.FromCategoryAndName("", "Razer BlackShark V2 Pro"));
        Assert.Equal(DeviceKind.Headset, DeviceClassifier.FromCategoryAndName("", "Razer Kraken"));
        Assert.Equal(DeviceKind.Mouse, DeviceClassifier.FromCategoryAndName("", "Razer DeathAdder V3"));
        Assert.Equal(DeviceKind.Mouse, DeviceClassifier.FromCategoryAndName("", "Razer Basilisk"));
        Assert.Equal(DeviceKind.Keyboard, DeviceClassifier.FromCategoryAndName("", "Razer Huntsman Mini"));
        Assert.Equal(DeviceKind.Keyboard, DeviceClassifier.FromCategoryAndName("", "Razer BlackWidow"));
        Assert.Equal(DeviceKind.Keyboard, DeviceClassifier.FromCategoryAndName("", "Razer Joro"));
        // Unknown product: Other.
        Assert.Equal(DeviceKind.Other, DeviceClassifier.FromCategoryAndName("", "Razer Chroma Mug"));
        Assert.Equal(DeviceKind.Mouse, DeviceClassifier.FromCategoryAndName("MOUSE_DOCK", "Razer Mouse Dock"));
    }
}
