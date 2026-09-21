//! Port of src/icons.rs: Segoe Fluent Icons glyphs (headset E7F6, keyboard
//! E765, mouse E962, gamepad E7FC) — tinted like the rest of the UI, Segoe
//! MDL2 Assets as the Win10 fallback. Rendering goes through the shared D2d
//! context (DirectWrite); WidthFor measures the ink from rendered pixels so
//! centering matches the pixels on screen.

using RazerTaskbar.Core;
using static RazerTaskbar.Native.Interop.DWriteConsts;

namespace RazerTaskbar.Native;

internal static class DeviceIcons
{
    /// <summary>The glyph for `kind` lives in Core (shared with WinUI FontIcons).</summary>
    public static char GlyphFor(DeviceKind kind) => DeviceIconGlyphs.For(kind);

    /// <summary>The icon font renders crisply only at these pixel sizes.</summary>
    private static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64];

    public static int SnapSize(int h)
    {
        var best = IconSizes[0];
        foreach (var s in IconSizes)
        {
            if (Math.Abs(s - h) <= Math.Abs(best - h))
            {
                best = s;
            }
        }
        return best;
    }

    /// <summary>Ink width of `kind`'s glyph at box height `h`, measured from
    /// the rendered pixels (advance width fallback); never narrower than a
    /// third of the box so tiny glyphs still get a sane column.</summary>
    public static int WidthFor(int h, DeviceKind kind)
        => Math.Max(D2d.InkWidth(GlyphFor(kind).ToString(),
            D2d.Format(h, TextAlignmentLeading, FontWeightNormal, icon: true)), h / 3);
}
