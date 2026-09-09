//! Port of src/icons.rs: Segoe Fluent Icons glyphs drawn as GDI text
//! (headset E7F6, keyboard E765, mouse E962, gamepad E7FC) — tinted like the
//! rest of the UI, Segoe MDL2 Assets as the Win10 fallback. Ink boxes are
//! scanned from rendered bitmaps so centering matches the pixels on screen.

using System.Runtime.InteropServices;
using RazerTaskbar.Core;
using static RazerTaskbar.Native.Interop.Gdi32;
using RazerTaskbar.Native.Interop;
using static RazerTaskbar.Native.Interop.User32;
using static RazerTaskbar.Native.Interop.Win32Consts;

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

    private static readonly object IconFontCacheLock = new();
    private static readonly Dictionary<int, IntPtr> IconFontCache = new();

    /// <summary>Cached icon font for `h` (snapped to Microsoft's magic sizes).
    /// Also serves the widget's battery glyph (same family + fallback).</summary>
    public static IntPtr IconFont(int h)
    {
        var snapped = SnapSize(h);
        lock (IconFontCacheLock)
        {
            if (IconFontCache.TryGetValue(snapped, out var handle))
            {
                return handle;
            }
            var font = CreateIconFont(snapped);
            IconFontCache[snapped] = font;
            return font;
        }
    }

    /// <summary>Create (caller owns/deletes) the icon font at pixel height
    /// `h`. Face-verified: CreateFontW never fails for an unknown face — it
    /// silently substitutes, and on zh-CN the substitute is SimSun — so the
    /// Win10 fallback (no Segoe Fluent Icons) must check the resolved face,
    /// not the handle (see GdiText.CreateTextFont).</summary>
    private static IntPtr CreateIconFont(int h)
    {
        var font = CreateFontW(h, 0, 0, 0, FW_NORMAL, 0, 0, 0,
            DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY,
            DEFAULT_PITCH | FF_DONTCARE, "Segoe Fluent Icons");
        if (!GdiText.FaceResolved(font, "Segoe Fluent Icons"))
        {
            DeleteObject(font);
            font = CreateFontW(h, 0, 0, 0, FW_NORMAL, 0, 0, 0,
                DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY,
                DEFAULT_PITCH | FF_DONTCARE, "Segoe MDL2 Assets");
        }
        return font;
    }

    private readonly record struct InkBox(int Left, int Right);

    private static readonly object InkCacheLock = new();
    private static readonly Dictionary<(int Size, DeviceKind Kind), InkBox> InkCache = new();
    private static bool _inkScanFailureLogged;

    private static InkBox? InkBoxOf(int h, DeviceKind kind)
    {
        var key = (Size: SnapSize(h), Kind: kind);
        lock (InkCacheLock)
        {
            if (InkCache.TryGetValue(key, out var hit))
            {
                return hit;
            }
        }
        var scanned = ScanInkBox(key.Size, kind);
        if (scanned is { } box)
        {
            lock (InkCacheLock)
            {
                InkCache[key] = box;
            }
        }
        else if (!_inkScanFailureLogged)
        {
            _inkScanFailureLogged = true;
            Log.Info("razer-taskbar: icon ink scan failed, falling back to ABC widths");
        }
        return scanned;
    }

    /// <summary>Render the glyph white-on-black into a memory DIB and scan the
    /// ink columns. Returns null when GDI could not produce the bitmap or the
    /// glyph drew nothing.</summary>
    private static InkBox? ScanInkBox(int h, DeviceKind kind)
    {
        int w = h * 3; // room for both side bearings
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
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h, // top-down rows
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                },
            };
            var hbmp = CreateDIBSection(mem, ref bmi, DIB_RGB_COLORS, out var bits, 0, 0);
            if (hbmp == 0)
            {
                DeleteDC(mem);
                return null;
            }
            var oldBmp = SelectObject(mem, hbmp);
            PatBlt(mem, 0, 0, w, h, BLACKNESS);
            var font = IconFont(h);
            var oldFont = SelectObject(mem, font);
            SetBkMode(mem, TRANSPARENT);
            SetTextColor(mem, 0x00FF_FFFF);
            // Drawn at column `originX` so a negative left bearing stays
            // inside the bitmap. DT_NOCLIP: the snapped font's line box can
            // exceed the layout rect.
            int originX = h;
            var buf = new[] { GlyphFor(kind) };
            var rc = new RECT { Left = originX, Top = 0, Right = w, Bottom = h };
            DrawTextW(mem, buf, buf.Length, ref rc, DT_SINGLELINE | DT_LEFT | DT_NOCLIP);

            int? minX = null;
            int maxX = originX;
            unsafe
            {
                var px = (uint*)bits;
                for (int x = 0; x < w; x++)
                {
                    for (int y = 0; y < h; y++)
                    {
                        uint p = px[(y * w) + x];
                        // White ink on black; grayscale AA edges count from 0x10 up.
                        if ((p & 0xFF) > 0x10 || ((p >> 8) & 0xFF) > 0x10 || ((p >> 16) & 0xFF) > 0x10)
                        {
                            minX ??= x;
                            maxX = x;
                            break;
                        }
                    }
                }
            }

            SelectObject(mem, oldFont);
            SelectObject(mem, oldBmp);
            DeleteObject(hbmp);
            DeleteDC(mem);
            return minX is { } l ? new InkBox(l - originX, maxX - originX) : null;
        }
        finally
        {
            ReleaseDC(0, screen);
        }
    }

    /// <summary>(origin→ink-left offset, ink width) at font height `h`:
    /// the scanned rasterization when available, else the (A, B) ABC widths.</summary>
    private static (int Off, int Width) InkMetrics(IntPtr hdc, int h, DeviceKind kind)
    {
        if (InkBoxOf(h, kind) is { } box)
        {
            return (box.Left, box.Right - box.Left + 1);
        }
        return GlyphMetrics(hdc, h, kind);
    }

    /// <summary>Ink width of `kind`'s glyph at box height `h`.</summary>
    public static int WidthFor(IntPtr hdc, int h, DeviceKind kind)
        => Math.Max(InkMetrics(hdc, h, kind).Width, h / 3);

    /// <summary>Draw `kind`'s ink starting at x (the glyph's origin is
    /// back-computed from the measured ink offset), vertically centered in
    /// box height `h`. Returns the ink width used.</summary>
    public static int Draw(IntPtr hdc, int x, int y, int h, DeviceKind kind, (byte r, byte g, byte b) rgb)
    {
        var (off, inkW) = InkMetrics(hdc, h, kind);
        inkW = Math.Max(inkW, h / 3);
        var font = IconFont(h);
        var oldFont = SelectObject(hdc, font);
        SetBkMode(hdc, TRANSPARENT);
        SetTextColor(hdc, GdiText.ColorRef(rgb.r, rgb.g, rgb.b));
        // DT_NOCLIP: the snapped font's line box is usually taller than the
        // layout box; without it DrawTextW shaves the glyph's top/bottom ink.
        var rect = new RECT { Left = x - off, Top = y, Right = x - off + (h * 2), Bottom = y + h };
        var buf = new[] { GlyphFor(kind) };
        DrawTextW(hdc, buf, buf.Length, ref rect, DT_SINGLELINE | DT_VCENTER | DT_LEFT | DT_NOCLIP);
        SelectObject(hdc, oldFont);
        return inkW;
    }

    /// <summary>(A side bearing, B ink width) of the glyph at font height `h`.</summary>
    private static (int A, int B) GlyphMetrics(IntPtr hdc, int h, DeviceKind kind)
    {
        var font = IconFont(h);
        var old = SelectObject(hdc, font);
        var ch = GlyphFor(kind);
        var ok = GetCharABCWidthsW(hdc, ch, ch, out var abc);
        SelectObject(hdc, old);
        return ok ? (abc.abcA, (int)abc.abcB) : (0, h / 2);
    }
}
