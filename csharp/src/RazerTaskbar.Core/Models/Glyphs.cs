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
