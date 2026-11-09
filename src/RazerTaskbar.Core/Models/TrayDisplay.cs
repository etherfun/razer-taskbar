// Notification-area icon display style: the battery glyph (legacy default) or
// the battery percentage as digits. Deliberately pure — the GDI rendering
// itself lives in RazerTaskbar/Native/TrayIcon.cs — so the setting's tolerant
// parsing and the label text are unit-testable without referencing the WinUI
// exe (same split as DisplayMode/BatteryType).

using System.Globalization;

namespace RazerTaskbar.Core;

public enum TrayDisplayMode
{
    /// <summary>Battery outline + level fill + charging bolt (the original icon).</summary>
    Icon,
    /// <summary>Battery percentage as digits ("--" when no device is known).</summary>
    Number,
}

/// <summary>Render inputs of one tray icon frame. `Level` is the displayed
/// device's percentage; a missing state (`null`) means "no device", which the
/// renderer paints gray. The state colors are shared with the widget's
/// two-layer battery icon (charging / saver / disconnected).</summary>
public readonly record struct TrayIconState(int Level, bool Charging, bool Connected, bool Saver);

public static class TrayDisplay
{
    public const string IconValue = "icon";
    public const string NumberValue = "number";

    /// <summary>Tolerant read of the `tray_display_mode` setting: missing,
    /// empty, mistyped or unknown values all fall back to the battery glyph —
    /// which is also the default for settings.json files written before the
    /// key existed, so old configs keep loading unchanged.</summary>
    public static TrayDisplayMode Parse(string? value)
        => value is not null && value.Trim().Equals(NumberValue, StringComparison.OrdinalIgnoreCase)
            ? TrayDisplayMode.Number
            : TrayDisplayMode.Icon;

    /// <summary>Canonical settings.json spelling ("icon" | "number").</summary>
    public static string ToConfig(this TrayDisplayMode mode)
        => mode == TrayDisplayMode.Number ? NumberValue : IconValue;

    /// <summary>Text drawn inside the 16x16 tray icon: "--" when there is no
    /// device, otherwise the level clamped to 0..100. At most three digits —
    /// the GDI renderer measures with DT_CALCRECT and shrinks the font until
    /// "100" fits, so the label is never truncated.</summary>
    public static string DisplayText(int? level)
        => level is { } value
            ? Math.Clamp(value, 0, 100).ToString(CultureInfo.InvariantCulture)
            : "--";
}
