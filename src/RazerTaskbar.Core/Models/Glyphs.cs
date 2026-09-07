// Port of the glyph/color parts of src/battery.rs (all three glyph series are
// 11-glyph sets, one per 10% step) plus the device-type icon codepoints from
// src/icons.rs (shared by the GDI layer and the WinUI FontIcons).

namespace RazerTaskbar.Core;

public enum BatteryGlyphState
{
    /// <summary>EBA0-EBAA: normal level batteries (0% .. 100%).</summary>
    Normal,
    /// <summary>EBAB-EBB5: charging batteries (bolt inside).</summary>
    Charging,
    /// <summary>EBB6-EBC0: battery-saver batteries (leaf inside).</summary>
    Saver,
}

public static class BatteryGlyphs
{
    /// <summary>Win11 battery glyph for `level` (0-100) in `state`.</summary>
    public static char BatteryGlyph(int level, BatteryGlyphState state)
    {
        int idx = (Math.Min(Math.Max(level, 0), 100) + 5) / 10; // 0..=10, rounded to 10%
        int baseCp = state switch
        {
            BatteryGlyphState.Normal => 0xEBA0,
            BatteryGlyphState.Charging => 0xEBAB,
            _ => 0xEBB6,
        };
        return (char)(baseCp + idx);
    }

    /// <summary>
    /// Layer 1 of the two-layer battery icon: the level glyph of the ACTIVE
    /// series (charging bolt EBAB- / saver leaf EBB6-) to tint with the
    /// state color; plain or disconnected devices get the normal EBA0-
    /// series. Layer 1 must come from the same series as the status overlay:
    /// the status glyphs cut their outline where the bolt/leaf crosses it,
    /// so a normal-series outline underneath would peek through those gaps.
    /// Saver wins over charging.
    /// </summary>
    public static char LevelGlyph(int level, bool connected, bool saver, bool charging)
        => !connected || (!saver && !charging) ? BatteryGlyph(level, BatteryGlyphState.Normal)
        : saver ? BatteryGlyph(level, BatteryGlyphState.Saver)
        : BatteryGlyph(level, BatteryGlyphState.Charging);

    /// <summary>
    /// Layer 2 of the two-layer battery icon: the 0% glyph of the same
    /// series as layer 1 (EBA0 plain outline / EBAB bolt / EBB6 leaf —
    /// outline + symbol, empty fill). Drawn in the default color on top of
    /// the tinted <see cref="LevelGlyph"/> it masks the tinted outline, so
    /// only the fill keeps its color — every connected device (plain
    /// discharge included) renders two-layer, or a high-charge battery
    /// would read as a solid colored blob. Null only when disconnected
    /// (gray single glyph); saver wins over charging.
    /// </summary>
    public static char? StatusOverlayGlyph(bool connected, bool saver, bool charging)
        => !connected ? null
        : saver ? BatteryGlyph(0, BatteryGlyphState.Saver)
        : charging ? BatteryGlyph(0, BatteryGlyphState.Charging)
        : BatteryGlyph(0, BatteryGlyphState.Normal);
}

public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public uint AsColorRef => (uint)(B << 16 | G << 8 | R); // COLORREF 0x00BBGGRR
    public uint AsRgb => (uint)(R << 16 | G << 8 | B);
}

public static class BatteryColors
{
    /// <summary>Win11 Fluent battery fill color by level.</summary>
    public static RgbColor ColorFor(int level) => level switch
    {
        <= 19 => new(0xE8, 0x11, 0x23), // red
        <= 39 => new(0xFF, 0x8C, 0x00), // orange
        <= 59 => new(0xFF, 0xB9, 0x00), // yellow
        <= 79 => new(0x6C, 0xCB, 0x5F), // light green
        _ => new(0x16, 0xC6, 0x0C),     // green
    };

    /// <summary>Layer-1 (level glyph) color of the two-layer battery icon:
    /// flat light green #9FD89F while charging, amber #EAA300 in device
    /// battery saver, gray when disconnected — those state colors show
    /// regardless of Settings → Colored battery icon. The option gates only
    /// the plain-discharge Win11 level tint (the green→red gradient); with
    /// it off, a plain connected device draws the default white glyph.
    /// Saver wins over charging.</summary>
    public static RgbColor LevelFillColor(int level, bool connected, bool saver, bool charging, bool colorize)
    {
        if (!connected) return new(0x80, 0x80, 0x80);
        if (saver) return new(0xEA, 0xA3, 0x00);    // #EAA300
        if (charging) return new(0x9F, 0xD8, 0x9F); // #9FD89F
        return colorize ? ColorFor(level) : new(0xFF, 0xFF, 0xFF);
    }
}

public static class DeviceIconGlyphs
{
    /// <summary>Segoe Fluent Icons / Segoe MDL2 Assets codepoints (icons.rs).</summary>
    public static char For(DeviceKind kind) => kind switch
    {
        DeviceKind.Headset => '\uE7F6',
        DeviceKind.Keyboard => '\uE765',
        DeviceKind.Mouse => '\uE962',
        _ => '\uE7FC', // gamepad/other
    };
}
