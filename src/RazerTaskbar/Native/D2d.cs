//! The single shared Direct2D/DirectWrite rendering context for every
//! native-drawn surface (widget overlay/band child, hover panel). Owns the
//! process-wide factories, font-face resolution, cached text formats and the
//! ink scanner — the D2D successor of GdiText's "measure the rendered
//! pixels" philosophy (declared glyph metrics disagree with the
//! rasterization; the scanner renders text through D2D into a scratch
//! premultiplied DIB and scans the alpha channel).
//!
//! Interop surface: Native/Interop/D2D1.cs + DWrite.cs (hand-written, FLAT
//! declarations — see the red lines in docs/agent-taskbar.md).

using System.Runtime.InteropServices;
using RazerTaskbar.Core;
using static RazerTaskbar.Native.Interop.Gdi32;
using RazerTaskbar.Native.Interop;
using static RazerTaskbar.Native.Interop.User32;
using static RazerTaskbar.Native.Interop.Win32Consts;

namespace RazerTaskbar.Native;

internal static class D2d
{
    // — process-wide graphics state (D2D factory is MULTI_THREADED, DWrite
    // SHARED; formats and scans are immutable/read-only after creation) —
    private static ID2D1Factory? _factory;
    private static IDWriteFactory? _dwrite;
    private static bool _failLogged;

    /// <summary>Create the factories once. False when D2D/DWrite is
    /// unavailable (logged once; callers skip painting rather than
    /// half-draw).</summary>
    public static bool EnsureGraphics()
    {
        if (_factory != null && _dwrite != null)
        {
            return true;
        }
        try
        {
            _factory ??= D2D1.CreateFactory();
            _dwrite ??= DWrite.CreateFactory();
            return _factory != null && _dwrite != null;
        }
        catch (Exception e)
        {
            if (!_failLogged)
            {
                _failLogged = true;
                Log.Error("razer-taskbar: D2D/DWrite init failed", e);
            }
            return false;
        }
    }

    /// <summary>A DC render target bound to nothing yet: the caller BindDCs
    /// it to its own 32bpp top-down DIB section. 96 dpi so 1 DIP == 1 px and
    /// the layout math stays integral.</summary>
    public static ID2D1DCRenderTarget CreateRenderTarget()
    {
        _factory!.CreateDCRenderTarget(new D2D1_RENDER_TARGET_PROPERTIES
        {
            Type = D2D1Consts.RenderTargetTypeDefault,
            PixelFormat = new D2D1_PIXEL_FORMAT
            {
                Format = D2D1Consts.DxiFormatB8G8R8A8Unorm,
                AlphaMode = D2D1Consts.AlphaModePremultiplied,
            },
            DpiX = 96f,
            DpiY = 96f,
        }, out var rt);
        return rt;
    }

    public static ID2D1SolidColorBrush CreateBrush(ID2D1DCRenderTarget rt)
    {
        var white = new D2D1_COLOR_F(1f, 1f, 1f, 1f);
        rt.CreateSolidColorBrush(ref white, IntPtr.Zero, out var brush);
        return brush;
    }

    public static void TryRelease(object? o)
    {
        if (o is null || !Marshal.IsComObject(o))
        {
            return;
        }
        try
        {
            Marshal.FinalReleaseComObject(o);
        }
        catch
        {
            // already released — nothing to tear down
        }
    }

    // — font faces —

    private static string? _textFace;
    private static string? _iconFace;

    /// <summary>Resolved family names. CreateFontW and DWrite both silently
    /// substitute unknown faces (SimSun on zh-CN — slab-serif Latin reads as
    /// pixel text at UI sizes), so the GDI probe is what actually picks the
    /// family once per process.</summary>
    public static string TextFace => _textFace ??= ProbeFace("Segoe UI Variable Text", "Segoe UI", FW_SEMIBOLD);
    public static string IconFace => _iconFace ??= ProbeFace("Segoe Fluent Icons", "Segoe MDL2 Assets", FW_NORMAL);

    private static string ProbeFace(string preferred, string fallback, int weight)
    {
        var f = CreateFontW(16, 0, 0, 0, weight, 0, 0, 0,
            DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY,
            DEFAULT_PITCH | FF_DONTCARE, preferred);
        bool ok = FaceResolved(f, preferred);
        DeleteObject(f);
        return ok ? preferred : fallback;
    }

    /// <summary>True when `font` is a live handle whose GDI-resolved face is
    /// `face`. CreateFontW accepts any face name and silently substitutes —
    /// a nonzero handle proves nothing.</summary>
    private static bool FaceResolved(IntPtr font, string face)
        => string.Equals(ActualFace(font), face, StringComparison.OrdinalIgnoreCase);

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

    // — text formats —

    private sealed record FormatKey(int Px, uint Align, uint Weight, bool Ellipsis, bool Icon);
    private static readonly Dictionary<FormatKey, IDWriteTextFormat> FormatCache = new();
    private static readonly List<IDWriteInlineObject> TrimmingSigns = new();

    /// <summary>Cached text format: vertically line-box-centered (paragraph
    /// center — the ink scanner corrects that to ink-centering per draw),
    /// no wrap, optional character-granularity ellipsis. Sizes are
    /// DPI-snapped by the callers so the key space stays tiny.</summary>
    public static IDWriteTextFormat Format(int px, uint align, uint weight, bool icon = false, bool ellipsis = false)
    {
        var key = new FormatKey(px, align, weight, ellipsis, icon);
        if (FormatCache.TryGetValue(key, out var f))
        {
            return f;
        }
        var face = icon ? IconFace : TextFace;
        _dwrite!.CreateTextFormat(face, IntPtr.Zero, weight, DWriteConsts.FontStyleNormal,
            DWriteConsts.FontStretchNormal, px, "en-US", out f);
        f.SetTextAlignment(align);
        f.SetParagraphAlignment(DWriteConsts.ParagraphAlignmentCenter);
        f.SetWordWrapping(DWriteConsts.WordWrappingNoWrap);
        if (ellipsis)
        {
            _dwrite.CreateEllipsisTrimmingSign(f, out var sign);
            TrimmingSigns.Add(sign);
            var trimming = new DWRITE_TRIMMING
            {
                Granularity = DWriteConsts.TrimmingGranularityCharacter,
                Delimiter = 0,
                DelimiterCount = 0,
            };
            f.SetTrimming(ref trimming, sign);
        }
        FormatCache[key] = f;
        return f;
    }

    /// <summary>Line-box height of `f`'s single-line text (the DWrite analog
    /// of GDI's tmHeight — the scan/draw box the ink deltas are relative
    /// to). Cached per format; formats live for the process.</summary>
    public static int LineHeight(IDWriteTextFormat f)
    {
        lock (LineHeightCache)
        {
            if (LineHeightCache.TryGetValue(f, out var h))
            {
                return h;
            }
        }
        _dwrite!.CreateTextLayout("0", 1, f, 10000f, 10000f, out var l);
        int lh;
        try
        {
            l.GetMetrics(out var m);
            lh = Math.Max((int)MathF.Ceiling(m.Height), 1);
        }
        finally
        {
            TryRelease(l);
        }
        lock (LineHeightCache)
        {
            LineHeightCache[f] = lh;
        }
        return lh;
    }

    private static readonly Dictionary<IDWriteTextFormat, int> LineHeightCache = new();

    /// <summary>Advance width of `s` in `f`. Empty strings are
    /// short-circuited: zero-length DrawText calls misbehave (crash fix
    /// carried over from hover.rs).</summary>
    public static int TextWidth(string s, IDWriteTextFormat f)
    {
        if (s.Length == 0)
        {
            return 0;
        }
        _dwrite!.CreateTextLayout(s, (uint)s.Length, f, 10000f, 10000f, out var l);
        try
        {
            l.GetMetrics(out var m);
            return (int)MathF.Ceiling(m.WidthIncludingTrailingWhitespace);
        }
        finally
        {
            TryRelease(l);
        }
    }

    // — ink scanning (rendered-pixel truth) —

    private static readonly object ScanLock = new();
    private static IntPtr _scanDc;
    private static IntPtr _scanBmp;
    private static IntPtr _scanBits;
    private static ID2D1DCRenderTarget? _scanRt;
    private static ID2D1SolidColorBrush? _scanBrush;
    private const int ScanW = 384, ScanH = 192;

    private sealed record InkBox(int Top, int Height, int Left, int Width);

    /// <summary>Single-glyph/short-string scan cache. Bounded: icon glyphs
    /// come from fixed sets and digits from ten values; labels (minutes
    /// churn) share the rendered line box per format, and multi-char scans
    /// are a few hundred microseconds anyway.</summary>
    private static readonly Dictionary<(IDWriteTextFormat, string), InkBox?> InkCache = new();
    /// <summary>Cap on <see cref="InkCache"/> — see ScanInk.</summary>
    private const int InkCacheMax = 2048;

    /// <summary>Render `text` white through D2D into the scratch premultiplied
    /// DIB (paragraph-centered in a line-height-tall box, same mode the real
    /// draws use) and scan the actual ink: its box relative to that box, or
    /// null when nothing rendered. This is the rendered truth — declared
    /// glyph metrics disagree with the rasterization (E850 battery: 8px
    /// declared, 10px rastered at 20px), which had icons visibly
    /// off-center once rows went ink-tight.</summary>
    private static InkBox? ScanInk(string text, IDWriteTextFormat f)
    {
        if (text.Length == 0 || !EnsureGraphics())
        {
            return null;
        }
        lock (ScanLock)
        {
            var key = (f, text);
            if (InkCache.TryGetValue(key, out var hit))
            {
                return hit;
            }
            // Bounded: the key carries the text, and label text churns (the
            // ETA string changes every minute), so an unbounded map grows for
            // the life of the process. A scan costs a few hundred microseconds,
            // so dropping the whole map at the cap is simpler than evicting.
            if (InkCache.Count >= InkCacheMax)
            {
                InkCache.Clear();
            }
            var scanned = ScanInkUncached(text, f);
            InkCache[key] = scanned;
            return scanned;
        }
    }

    private static InkBox? ScanInkUncached(string text, IDWriteTextFormat f)
    {
        if (_scanRt == null && !EnsureScanSurface())
        {
            return null;
        }
        int boxH = LineHeight(f);
        int slack = boxH * 2 + 4;
        int w = slack * 2 + Math.Max(TextWidth(text, f), 1);
        if (w > ScanW || boxH > ScanH)
        {
            return null; // scan surface too small — callers fall back to line box
        }
        var rt = _scanRt!;
        var brush = _scanBrush!;
        var full = new RECT { Left = 0, Top = 0, Right = ScanW, Bottom = ScanH };
        rt.BindDC(_scanDc, ref full);
        rt.BeginDraw();
        try
        {
            var transparent = new D2D1_COLOR_F(0f, 0f, 0f, 0f);
            rt.Clear(ref transparent);
            rt.SetTextAntialiasMode(D2D1Consts.TextAntialiasModeGrayscale);
            var white = new D2D1_COLOR_F(1f, 1f, 1f, 1f);
            brush.SetColor(ref white);
            var rect = new D2D1_RECT_F(slack, 0, slack + Math.Max(TextWidth(text, f), 1), boxH);
            rt.DrawText(text, (uint)text.Length, f, ref rect, brush,
                D2D1Consts.DrawTextOptionsNone, DWriteConsts.MeasuringModeNatural);
        }
        finally
        {
            rt.EndDraw(out _, out _);
        }

        int top = -1, bot = -1, minX = -1, maxX = -1;
        // Scan the DIB in place: the alpha channel is read straight out of the
        // mapped section. The managed copy this replaces was 294,912 bytes per
        // miss — over the LOH threshold, so every uncached scan (the ETA label
        // churns once a minute) left a large-object allocation behind.
        unsafe
        {
            var px = (byte*)_scanBits;
            for (int y = 0; y < ScanH; y++)
            {
                int row = y * ScanW * 4;
                for (int x = 0; x < ScanW; x++)
                {
                    if (px[row + x * 4 + 3] > 0x10)
                    {
                        if (top < 0)
                        {
                            top = y;
                        }
                        bot = y;
                        minX = minX < 0 ? x : Math.Min(minX, x);
                        maxX = Math.Max(maxX, x);
                    }
                }
            }
        }
        return top < 0 ? null : new InkBox(top, bot - top + 1, minX - slack, maxX - minX + 1);
    }

    private static bool EnsureScanSurface()
    {
        if (_scanRt != null)
        {
            return true;
        }
        if (!EnsureGraphics())
        {
            return false;
        }
        var wdc = GetDC(0);
        if (wdc == 0)
        {
            return false;
        }
        _scanDc = CreateCompatibleDC(wdc);
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = ScanW,
                biHeight = -ScanH,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            },
        };
        _scanBmp = CreateDIBSection(wdc, ref bmi, DIB_RGB_COLORS, out _scanBits, 0, 0);
        ReleaseDC(0, wdc);
        if (_scanDc == 0 || _scanBmp == 0 || _scanBits == 0)
        {
            DestroyScanSurface();
            return false;
        }
        SelectObject(_scanDc, _scanBmp);
        try
        {
            _scanRt = CreateRenderTarget();
            var full = new RECT { Left = 0, Top = 0, Right = ScanW, Bottom = ScanH };
            _scanRt.BindDC(_scanDc, ref full);
            _scanBrush = CreateBrush(_scanRt);
            return true;
        }
        catch (Exception e)
        {
            Log.Error("razer-taskbar: D2D scan surface creation failed", e);
            DestroyScanSurface();
            return false;
        }
    }

    private static void DestroyScanSurface()
    {
        TryRelease(_scanBrush);
        _scanBrush = null;
        TryRelease(_scanRt);
        _scanRt = null;
        if (_scanDc != 0)
        {
            DeleteDC(_scanDc);
            _scanDc = 0;
        }
        if (_scanBmp != 0)
        {
            DeleteObject(_scanBmp);
            _scanBmp = 0;
        }
        _scanBits = 0;
    }

    /// <summary>Vertical shift (px) so a paragraph-centered draw of `text`
    /// centers the string's actual INK rather than its line box: line boxes
    /// carry asymmetric padding (the icon fonts report ascent-only boxes),
    /// so digits and Fluent icons ride visibly high inside tight rows.
    /// Measured from the RENDERED pixels (see <see cref="ScanInk"/>).
    /// 0 when nothing rendered (plain line-box centering).</summary>
    public static int InkCenterDelta(string text, IDWriteTextFormat f)
    {
        int boxH = LineHeight(f);
        if (ScanInk(text, f) is not { } box)
        {
            return 0;
        }
        return (boxH - box.Height) / 2 - box.Top;
    }

    /// <summary>Tight ink height of `text` measured from the RENDERED pixels;
    /// falls back to the line-box height when nothing rendered — callers
    /// substitute it the same way the GDI pipeline used the cell height.</summary>
    public static int InkHeight(string text, IDWriteTextFormat f)
    {
        return ScanInk(text, f) is { } box ? box.Height : LineHeight(f);
    }

    /// <summary>Ink width of `text` measured from the RENDERED pixels;
    /// advance width as the fallback.</summary>
    public static int InkWidth(string text, IDWriteTextFormat f)
    {
        return ScanInk(text, f) is { } box ? box.Width : TextWidth(text, f);
    }

    /// <summary>Left bearing of the ink relative to the draw origin — how
    /// far the origin must back up so the ink starts at the intended x.</summary>
    public static int InkLeft(string text, IDWriteTextFormat f)
    {
        return ScanInk(text, f)?.Left ?? 0;
    }

    /// <summary>Single-line text at `x`, vertically centered in `rect` by its
    /// INK. `inkDelta` overrides the per-text ink centering so several
    /// layers can share one alignment — e.g. the battery's status overlay
    /// must center by the LEVEL glyph's ink box, not its own (the bolt/leaf
    /// pokes above the outline, which would misalign the two).</summary>
    public static void DrawInkText(
        ID2D1DCRenderTarget rt,
        ID2D1SolidColorBrush brush,
        IDWriteTextFormat format,
        string text,
        int x,
        int top, int bottom,
        D2D1_COLOR_F color,
        int? inkDelta = null)
    {
        if (text.Length == 0)
        {
            return; // zero-length DrawText misbehaves (crash fix from hover.rs)
        }
        int dy = inkDelta ?? InkCenterDelta(text, format);
        brush.SetColor(ref color);
        var rect = new D2D1_RECT_F(x, top + dy, int.Max(x + 4, x + TextWidth(text, format)), bottom + dy);
        rt.DrawText(text, (uint)text.Length, format, ref rect, brush,
            D2D1Consts.DrawTextOptionsNone, DWriteConsts.MeasuringModeNatural);
    }
}
