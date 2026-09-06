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
        // Normal: EBA0-EBAA, one glyph per 10% (EBAA is truly full).
        Assert.Equal('\uEBA0', BatteryGlyphs.BatteryGlyph(0, BatteryGlyphState.Normal));
        Assert.Equal('\uEBA7', BatteryGlyphs.BatteryGlyph(72, BatteryGlyphState.Normal));
        Assert.Equal('\uEBAA', BatteryGlyphs.BatteryGlyph(100, BatteryGlyphState.Normal));
        // Charging / saver: 11-glyph series, 100% hits the last one.
        Assert.Equal('\uEBAB', BatteryGlyphs.BatteryGlyph(0, BatteryGlyphState.Charging));
        Assert.Equal('\uEBB2', BatteryGlyphs.BatteryGlyph(72, BatteryGlyphState.Charging));
        Assert.Equal('\uEBB5', BatteryGlyphs.BatteryGlyph(100, BatteryGlyphState.Charging));
        Assert.Equal('\uEBB9', BatteryGlyphs.BatteryGlyph(30, BatteryGlyphState.Saver));
        Assert.Equal('\uEBC0', BatteryGlyphs.BatteryGlyph(100, BatteryGlyphState.Saver));
        // Rounding: 0-4% stays on the empty glyph, 5% rounds up a step.
        Assert.Equal('\uEBA0', BatteryGlyphs.BatteryGlyph(4, BatteryGlyphState.Normal));
        Assert.Equal('\uEBA1', BatteryGlyphs.BatteryGlyph(5, BatteryGlyphState.Normal));
    }

    [Fact]
    public void StatusOverlayGlyphPicksZeroFillStatusGlyph()
    {
        // Two-layer icon: the overlay is the 0% glyph of layer 1's series
        // (outline + symbol, empty fill) that masks the tinted outline.
        // Disconnected rows draw the gray level glyph only.
        Assert.Null(BatteryGlyphs.StatusOverlayGlyph(false, false, false));
        Assert.Null(BatteryGlyphs.StatusOverlayGlyph(false, true, true));
        // Plain discharge overlays the plain EBA0 outline (otherwise a high
        // charge would read as a solid colored blob); charging: EBAB (bolt);
        // saver wins over charging: EBB6 (leaf).
        Assert.Equal('\uEBA0', BatteryGlyphs.StatusOverlayGlyph(true, false, false));
        Assert.Equal('\uEBAB', BatteryGlyphs.StatusOverlayGlyph(true, false, true));
        Assert.Equal('\uEBB6', BatteryGlyphs.StatusOverlayGlyph(true, true, false));
        Assert.Equal('\uEBB6', BatteryGlyphs.StatusOverlayGlyph(true, true, true));
    }

    [Fact]
    public void LevelGlyphUsesActiveSeries()
    {
        // Layer 1 must come from the same series as the status overlay: the
        // status glyphs cut their outline where the bolt/leaf crosses it,
        // and a normal-series outline underneath would peek through.
        Assert.Equal('\uEBA5', BatteryGlyphs.LevelGlyph(50, true, false, false));
        Assert.Equal('\uEBA5', BatteryGlyphs.LevelGlyph(50, false, true, true));
        // Charging 50% = EBB0, saver 50% = EBBB; saver wins over charging.
        Assert.Equal('\uEBB0', BatteryGlyphs.LevelGlyph(50, true, false, true));
        Assert.Equal('\uEBBB', BatteryGlyphs.LevelGlyph(50, true, true, true));
    }

    [Fact]
    public void LevelFillColorUsesStateColors()
    {
        // Charging: flat #9FD89F regardless of level; saver: #EAA300 and
        // wins over charging; plain discharge keeps the level tint;
        // disconnected is gray.
        Assert.Equal(new RgbColor(0x9F, 0xD8, 0x9F), BatteryColors.LevelFillColor(50, true, false, true));
        Assert.Equal(new RgbColor(0x9F, 0xD8, 0x9F), BatteryColors.LevelFillColor(90, true, false, true));
        Assert.Equal(new RgbColor(0xEA, 0xA3, 0x00), BatteryColors.LevelFillColor(50, true, true, true));
        Assert.Equal(BatteryColors.ColorFor(72), BatteryColors.LevelFillColor(72, true, false, false));
        Assert.Equal(new RgbColor(0x80, 0x80, 0x80), BatteryColors.LevelFillColor(50, false, false, true));
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
