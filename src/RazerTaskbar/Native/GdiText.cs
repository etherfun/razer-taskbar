//! Port of the text-drawing parts of src/window.rs: cached UI text fonts
//! (Segoe UI Variable Text, falling back to Segoe UI), ink-based vertical
//! centering (GetGlyphOutlineW GGO_METRICS) and the shared ink-centered draw.
//! Fonts live until process exit; GDI font handles are process-wide.

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
            var f = CreateTextFont(height, weight);
            FontCache[(height, weight)] = f;
            return f;
        }
    }

    /// <summary>Create one UI text font handle (caller owns/deletes it).
    /// Grayscale ANTIALIASED_QUALITY: the widget composites per-pixel alpha,
    /// where a ClearType sub-pixel fringe would turn into an opaque colored
    /// speckle ring after the coverage→alpha transform (ClearType presupposes
    /// an opaque background). Negative `height` requests em height — positive
    /// heights shrink the em by internal leading and hinting gets crunchy.
    /// Face verification: CreateFontW never fails for an unknown face — it
    /// silently substitutes, and on zh-CN systems the substitute is SimSun,
    /// whose slab-serif Latin digits read as pixel text at UI sizes. Only if
    /// the variable face did not resolve, fall back to static Segoe UI
    /// (Microsoft's recommended face for GDI desktop apps).</summary>
    public static IntPtr CreateTextFont(int height, int weight)
    {
        var f = CreateFontW(-height, 0, 0, 0, weight, 0, 0, 0,
            DEFAULT_CHARSET, OUT_TT_ONLY_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY,
            DEFAULT_PITCH | FF_DONTCARE, "Segoe UI Variable Text");
        if (!string.Equals(ActualFace(f), "Segoe UI Variable Text", StringComparison.OrdinalIgnoreCase))
        {
            DeleteObject(f);
            f = CreateFontW(-height, 0, 0, 0, weight, 0, 0, 0,
                DEFAULT_CHARSET, OUT_TT_ONLY_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY,
                DEFAULT_PITCH | FF_DONTCARE, "Segoe UI");
            if (!string.Equals(ActualFace(f), "Segoe UI", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("razer-taskbar: text font fell back to " + ActualFace(f));
            }
        }
        return f;
    }

    /// <summary>Face name GDI resolved for `font` ("" when unknowable).
    /// Requires a screen DC to select the font into; one-shot per font.</summary>
    private static string ActualFace(IntPtr font)
    {
        if (font == 0)
        {
            return "";
        }
        var hdc = GetDC(0);
        if (hdc == 0)
        {
            return "";
        }
        try
        {
            var old = SelectObject(hdc, font);
            var sb = new System.Text.StringBuilder(64);
            bool ok = GetTextFaceW(hdc, sb.Capacity, sb);
            SelectObject(hdc, old);
            return ok ? sb.ToString() : "";
        }
        finally
        {
            ReleaseDC(0, hdc);
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

    /// <summary>Tight ink height of `text` (union GGO_METRICS box, same
    /// walk as <see cref="InkCenterDelta"/>): 0 when the metrics are
    /// unavailable — callers fall back to the line-box height.</summary>
    public static int InkHeight(IntPtr hdc, char[] text)
    {
        if (text.Length == 0)
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
        return top >= int.MinValue / 2 ? top + depth : 0;
    }

    /// <summary>Single-line text at `x`, vertically centered in `rect` by its
    /// INK. `inkDelta` overrides the per-text ink centering so several
    /// layers can share one alignment — e.g. the battery's status overlay
    /// must center by the LEVEL glyph's ink box, not its own (the bolt/leaf
    /// pokes above the outline, which would misalign the two).</summary>
    public static void DrawInkText(
        IntPtr hdc,
        IntPtr font,
        char[] text,
        int x,
        RECT rect,
        uint color,
        int? inkDelta = null)
    {
        SelectObject(hdc, font);
        int dy = inkDelta ?? InkCenterDelta(hdc, text);
        var r = new RECT
        {
            Left = x,
            Top = rect.Top + dy,
            Right = rect.Right,
            Bottom = rect.Bottom + dy,
        };
        SetTextColor(hdc, color);
        DrawTextW(hdc, text, text.Length, ref r, DT_SINGLELINE | DT_VCENTER | DT_LEFT);
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
