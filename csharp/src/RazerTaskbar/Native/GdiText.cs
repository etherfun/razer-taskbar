//! Port of the text-drawing parts of src/window.rs: cached UI text fonts
//! (Segoe UI Variable Text, falling back to Segoe UI), ink-based vertical
//! centering (GetGlyphOutlineW GGO_METRICS), and the shared 1px-drop-shadow
//! draw. Fonts live until process exit; GDI font handles are process-wide.

using System.Runtime.InteropServices;
using static RazerTaskbar.Native.Interop.Gdi32;
using RazerTaskbar.Native.Interop;
using static RazerTaskbar.Native.Interop.User32;
using static RazerTaskbar.Native.Interop.Win32Consts;

namespace RazerTaskbar.Native;

internal static class GdiText
{
    private static readonly object FontCacheLock = new();
    private static readonly Dictionary<(int Height, int Weight), IntPtr> FontCache = new();

    /// <summary>Cached UI text font keyed by (pixel height, weight).</summary>
    public static IntPtr CachedTextFont(int height, int weight)
    {
        lock (FontCacheLock)
        {
            if (FontCache.TryGetValue((height, weight), out var handle))
            {
                return handle;
            }
            var f = CreateFontW(height, 0, 0, 0, weight, 0, 0, 0,
                DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY,
                DEFAULT_PITCH | FF_DONTCARE, "Segoe UI Variable Text");
            // Fallback if the Variable font is missing (Win10 / older Win11).
            if (f == 0)
            {
                f = CreateFontW(height, 0, 0, 0, weight, 0, 0, 0,
                    DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY,
                    DEFAULT_PITCH | FF_DONTCARE, "Segoe UI");
            }
            FontCache[(height, weight)] = f;
            return f;
        }
    }

    /// <summary>Vertical shift (px) so a DT_VCENTER draw of `text` centers the
    /// string's actual INK rather than its line box: line boxes carry
    /// asymmetric descent padding, so digits and Fluent icons ride visibly
    /// high inside tight rows. Union ink box via GGO_METRICS, relative to the
    /// line box of the font currently selected in `hdc`; 0 when unavailable.</summary>
    public static int InkCenterDelta(IntPtr hdc, char[] text)
    {
        if (!GetTextMetricsW(hdc, out var tm) || text.Length == 0)
        {
            return 0;
        }
        int top = int.MinValue;
        int depth = int.MinValue;
        var mat = new MAT2
        {
            eM11 = new FIXED { fract = 0, value = 1 },
            eM12 = new FIXED { fract = 0, value = 0 },
            eM21 = new FIXED { fract = 0, value = 0 },
            eM22 = new FIXED { fract = 0, value = 1 },
        };
        foreach (var ch in text)
        {
            if (GetGlyphOutlineW(hdc, ch, GGO_METRICS, out var gm, 0, 0, ref mat) != GDI_ERROR)
            {
                top = Math.Max(top, gm.gmptGlyphOrigin.Y);
                depth = Math.Max(depth, (int)gm.gmBlackBoxY - gm.gmptGlyphOrigin.Y);
            }
        }
        if (top >= int.MinValue / 2)
        {
            return ((tm.tmDescent - tm.tmAscent) - (depth - top)) / 2;
        }
        return 0;
    }

    /// <summary>Single-line text at `x`, vertically centered in `rect` by its
    /// INK, over a 1px drop shadow drawn first (never pure black — the color
    /// key would cut it out).</summary>
    public static void DrawShadowedText(
        IntPtr hdc,
        IntPtr font,
        char[] text,
        int x,
        RECT rect,
        uint color,
        uint shadow)
    {
        SelectObject(hdc, font);
        int dy = InkCenterDelta(hdc, text);
        var r = new RECT
        {
            Left = x + 1,
            Top = rect.Top + 1 + dy,
            Right = rect.Right,
            Bottom = rect.Bottom + dy,
        };
        SetTextColor(hdc, shadow);
        DrawTextW(hdc, text, text.Length, ref r, DT_SINGLELINE | DT_VCENTER | DT_LEFT);
        var r2 = new RECT
        {
            Left = x,
            Top = rect.Top + dy,
            Right = rect.Right,
            Bottom = rect.Bottom + dy,
        };
        SetTextColor(hdc, color);
        DrawTextW(hdc, text, text.Length, ref r2, DT_SINGLELINE | DT_VCENTER | DT_LEFT);
    }

    /// <summary>Width of `s` in the font selected on `hdc`. Empty strings are
    /// short-circuited: DrawTextW with a zero-char buffer access-violates in
    /// text shaping (crash fix carried over from hover.rs).</summary>
    public static int TextWidth(IntPtr hdc, string s)
    {
        if (s.Length == 0)
        {
            return 0;
        }
        var wide = s.ToCharArray();
        var rc = new RECT();
        DrawTextW(hdc, wide, wide.Length, ref rc, DT_SINGLELINE | DT_CALCRECT | DT_LEFT);
        return rc.Right - rc.Left;
    }

    public static uint ColorRef(byte r, byte g, byte b)
        => (uint)(r | (g << 8) | (b << 16));
}
