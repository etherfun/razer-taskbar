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
