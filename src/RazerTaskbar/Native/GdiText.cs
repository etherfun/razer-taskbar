//! Port of the text-drawing parts of src/window.rs: cached UI text fonts
//! (Segoe UI Variable Text, falling back to Segoe UI), ink-based vertical
//! centering and ink measurement from RENDERED pixels (GGO_METRICS
//! under-reports the Fluent battery glyphs — E850 declares 8px, rasterizes
//! 10px at 20px — which had icons visibly off-center once rows went
//! ink-tight), and the shared ink-centered draw.
//! Fonts live until process exit; GDI font handles are process-wide.

using System.Runtime.InteropServices;
using RazerTaskbar.Core;
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
        if (!FaceResolved(f, "Segoe UI Variable Text"))
        {
            DeleteObject(f);
            f = CreateFontW(-height, 0, 0, 0, weight, 0, 0, 0,
                DEFAULT_CHARSET, OUT_TT_ONLY_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY,
                DEFAULT_PITCH | FF_DONTCARE, "Segoe UI");
            if (!FaceResolved(f, "Segoe UI"))
            {
                Log.Info("razer-taskbar: text font fell back to " + ActualFace(f));
            }
        }
        return f;
    }

    /// <summary>True when `font` is a live handle whose GDI-resolved face is
    /// `face`. The verification is the point: CreateFontW accepts any face
    /// name and silently substitutes (see <see cref="CreateTextFont"/>), so
    /// a nonzero handle proves nothing — a `font == 0` fallback never fires
    /// and glyphs render in whatever the substitute turned out to be.</summary>
    internal static bool FaceResolved(IntPtr font, string face)
        => string.Equals(ActualFace(font), face, StringComparison.OrdinalIgnoreCase);

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

    private static readonly object InkScanLock = new();
    /// <summary>Rendered ink boxes for single glyphs, keyed by (font pixel
    /// height, char). Bounded: icon glyphs come from fixed sets. Multi-char
    /// strings (labels) are scanned uncached — labels churn every minute and
    /// a scan is a few microseconds.</summary>
    private static readonly Dictionary<(int Size, char Ch), (int Top, int Height)?> InkScanCache = new();

    /// <summary>Render `text` white-on-black with the font currently selected
    /// on `hdc` (DT_VCENTER in a tmHeight-tall box) and scan the actual ink
    /// rows: (top, height) of the ink inside that box, or null when nothing
    /// rendered. This is the rendered truth — the declared metrics
    /// (GGO_METRICS) do not match the rasterized glyphs (see
    /// <see cref="InkHeight"/>).</summary>
    private static (int Top, int Height)? ScanInkBox(IntPtr hdc, char[] text)
    {
        if (!GetTextMetricsW(hdc, out var tm) || text.Length == 0 || tm.tmHeight <= 0)
        {
            return null;
        }
        int h = tm.tmHeight;
        if (text.Length > 1)
        {
            return ScanInkBoxUncached(hdc, text, h);
        }
        var key = (h, text[0]);
        lock (InkScanLock)
        {
            if (InkScanCache.TryGetValue(key, out var hit))
            {
                return hit;
            }
        }
        var scanned = ScanInkBoxUncached(hdc, text, h);
        lock (InkScanLock)
        {
            InkScanCache[key] = scanned;
        }
        return scanned;
    }

    private static (int Top, int Height)? ScanInkBoxUncached(IntPtr hdc, char[] text, int h)
    {
        var font = GetCurrentObject(hdc, OBJ_FONT);
        if (font == 0)
        {
            return null;
        }
        var screen = GetDC(0);
        if (screen == 0)
        {
            return null;
        }
        try
        {
            var mem = CreateCompatibleDC(screen);
            if (mem == 0)
            {
                return null;
            }
            var oldFont = SelectObject(mem, font);
            // Size the bitmap to the text: h slack on each side so negative
            // left bearings stay inside, plus the measured advance width.
            var mrc = new RECT();
            DrawTextW(mem, text, text.Length, ref mrc, DT_SINGLELINE | DT_CALCRECT | DT_LEFT);
            int w = h + Math.Max(mrc.Right - mrc.Left, 1) + h;
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                },
            };
            var hbmp = CreateDIBSection(mem, ref bmi, DIB_RGB_COLORS, out var bits, 0, 0);
            if (hbmp == 0)
            {
                SelectObject(mem, oldFont);
                DeleteDC(mem);
                return null;
            }
            var oldBmp = SelectObject(mem, hbmp);
            PatBlt(mem, 0, 0, w, h, BLACKNESS);
            SetBkMode(mem, TRANSPARENT);
            SetTextColor(mem, 0x00FF_FFFF);
            // DT_NOCLIP: the (usually taller) icon-font line box cannot shave
            // ink; the box is [0,h], plain DT_VCENTER, x=h.
            var rc = new RECT { Left = h, Top = 0, Right = w, Bottom = h };
            DrawTextW(mem, text, text.Length, ref rc, DT_SINGLELINE | DT_VCENTER | DT_LEFT | DT_NOCLIP);
            int bytes = w * h * 4;
            var px = new byte[bytes];
            System.Runtime.InteropServices.Marshal.Copy(bits, px, 0, bytes);
            int top = -1;
            int bot = -1;
            for (int y = 0; y < h && top < 0; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    if (px[i] > 0x10 || px[i + 1] > 0x10 || px[i + 2] > 0x10)
                    {
                        top = y;
                        break;
                    }
                }
            }
            for (int y = h - 1; y >= 0 && bot < 0; y--)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    if (px[i] > 0x10 || px[i + 1] > 0x10 || px[i + 2] > 0x10)
                    {
                        bot = y;
                        break;
                    }
                }
            }
            SelectObject(mem, oldBmp);
            SelectObject(mem, oldFont);
            DeleteObject(hbmp);
            DeleteDC(mem);
            return top < 0 ? null : (top, bot - top + 1);
        }
        finally
        {
            ReleaseDC(0, screen);
        }
    }

    /// <summary>Vertical shift (px) so a DT_VCENTER draw of `text` centers the
    /// string's actual INK rather than its line box: line boxes carry
    /// asymmetric padding (the icon fonts report ascent-only boxes), so
    /// digits and Fluent icons ride visibly high inside tight rows. Measured
    /// from the RENDERED pixels (see <see cref="ScanInkBox"/>) — the declared
    /// glyph metrics disagree with the rasterization (E850 battery: 8px
    /// declared, 10px rastered at 20px), so with the GGO numbers the icons
    /// sat visibly off-center. 0 when nothing rendered (plain line-box
    /// centering).</summary>
    public static int InkCenterDelta(IntPtr hdc, char[] text)
    {
        if (ScanInkBox(hdc, text) is not { } box || !GetTextMetricsW(hdc, out var tm))
        {
            return 0;
        }
        // Plain DT_VCENTER put the ink at box.Top; shift it to the middle.
        return (tm.tmHeight - box.Height) / 2 - box.Top;
    }

    /// <summary>Tight ink height of `text` measured from the RENDERED pixels
    /// (see <see cref="ScanInkBox"/>); GGO_METRICS union box as the fallback
    /// when nothing rendered, then 0 — callers substitute the line-box
    /// height. Companion to <see cref="InkCenterDelta"/>.</summary>
    public static int InkHeight(IntPtr hdc, char[] text)
    {
        if (ScanInkBox(hdc, text) is { } box)
        {
            return box.Height;
        }
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
