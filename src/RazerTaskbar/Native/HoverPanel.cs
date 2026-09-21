//! Port of src/hover.rs: hover popover listing every known Razer device
//! (name, battery, charging). The widget overlay is fully click-through, so
//! hover is detected by polling the cursor against the widget rect on the
//! fast TIMER_HOVER tick (120ms). The panel is a topmost layered NOACTIVATE
//! window that never takes focus and is itself click-through.
//!
//! Painting is Direct2D + DirectWrite through the shared D2d context (no
//! GDI text): DWrite ink lands on a premultiplied-ARGB DIB through a DC
//! render target, so every AA edge — text, glyphs, card corners, border —
//! carries true partial alpha and composites with the real backdrop. The
//! old GDI pipeline forced text AA pixels fully opaque over the translucent
//! card (a color-key-style dirty fringe); the sentinel-color classification
//! in Present is gone with it.

using System.Diagnostics;
using System.Runtime.InteropServices;
using RazerTaskbar.Core;
using static RazerTaskbar.Native.Interop.Gdi32;
using RazerTaskbar.Native.Interop;
using static RazerTaskbar.Native.Interop.User32;
using static RazerTaskbar.Native.Interop.Win32Consts;
using static RazerTaskbar.Native.Interop.DWriteConsts;

namespace RazerTaskbar.Native;

public static class HoverPanel
{
    private const string ClassName = "RazerTaskbarHover";

    /// <summary>Rest the cursor on the widget this long before the panel appears.</summary>
    private static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(350);

    /// <summary>One panel row: pre-rendered strings + drawing state (snapshot
    /// — painting never touches the device map).</summary>
    private sealed record Row(
        string Name,
        string Pct,
        /// <summary>Predicted usage time / time-to-full (compact); empty when
        /// history has not enough data or the device is disconnected.</summary>
        string Eta,
        int Level,
        bool Charging,
        bool Saver,
        bool Connected,
        bool Displayed,
        DeviceKind Kind);

    // Widget-thread-only state (same pattern as WidgetWindow.State).
    private static Stopwatch? _hoverSince;
    private static bool _shown;
    private static List<Row> _rows = new();
    private static (int X, int Y) _panelPos;
    private static (int W, int H) _panelSize;
    private static float _lastScale;
    private static IntPtr _hoverWnd;

    // Offscreen 32bpp DIB the panel paints into before the per-pixel-alpha
    // upload (see EnsureSurface / Present) + its D2D render target.
    private static IntPtr _memDc;
    private static IntPtr _memBmp;
    private static IntPtr _memBits;
    private static int _memW;
    private static int _memH;
    private static bool _ulwFailLogged;
    private static ID2D1DCRenderTarget? _rt;
    private static ID2D1SolidColorBrush? _brush;

    /// <summary>Fast timer tick: show/hide/update the panel from cursor proximity.</summary>
    public static void Track(IntPtr widget, bool enabled)
    {
        if (!enabled)
        {
            Hide();
            return;
        }
        var wrect = TaskbarLocator.WindowRect(widget);
        if (wrect is not { } wr)
        {
            Hide();
            return;
        }
        GetCursorPos(out var pt);
        bool overWidget = PtInRect(ref wr, pt);
        var pr = PanelRect();
        bool overPanel = _shown && PtInRect(ref pr, pt);
        if (!overWidget && !overPanel)
        {
            Hide();
            return;
        }
        // Occlusion gate (widget path only): the widget rect survives a
        // fullscreen video covering the auto-hidden taskbar, but the panel
        // must not pop over the covering window. WindowFromPoint resolves
        // the point through WM_NCHITTEST (skipping click-through overlays)
        // — when the top-level window beneath the cursor is not the one the
        // widget belongs to, the widget is covered: hide. Hovering the
        // panel itself skips this gate (it floats above arbitrary
        // backdrops and is HTTRANSPARENT anyway).
        if (overWidget
            && GetAncestor(WindowFromPoint(pt), GA_ROOT) != GetAncestor(widget, GA_ROOT))
        {
            Hide();
            return;
        }
        if (_hoverSince is null)
        {
            _hoverSince = Stopwatch.StartNew();
        }
        bool hoveredLong = _hoverSince.Elapsed >= HoverDelay;
        if (!_shown && !hoveredLong)
        {
            return;
        }
        UpdateAndShow(widget, wr, pt);
    }

    /// <summary>Hide the panel (cursor left, or the feature was switched off).</summary>
    public static void Hide()
    {
        _hoverSince = null;
        if (_shown)
        {
            _shown = false;
            if (_hoverWnd != 0)
            {
                ShowWindow(_hoverWnd, SW_HIDE);
            }
        }
    }

    public static void Destroy()
    {
        _hoverSince = null;
        _shown = false;
        _rows.Clear();
        DestroySurface();
        if (_hoverWnd != 0)
        {
            DestroyWindow(_hoverWnd);
            _hoverWnd = 0;
        }
    }

    private static RECT PanelRect() => new()
    {
        Left = _panelPos.X,
        Top = _panelPos.Y,
        Right = _panelPos.X + _panelSize.W,
        Bottom = _panelPos.Y + _panelSize.H,
    };

    private static Row NoDevicesRow() => new(
        I18n.Tr("No Razer devices found"), "--", "", 0, false, false, false, false, DeviceKind.Other);

    /// <summary>Immutable snapshot of the device list: connected first (then by
    /// name). The device the widget currently shows gets the highlight.</summary>
    private static List<Row> SnapshotRows()
    {
        var devices = AppState.Instance.Devices.Snapshot();
        if (devices.Count == 0)
        {
            return [NoDevicesRow()];
        }
        // Follow the display mode's pick (drop-swap override / rotate cursor)
        // without advancing it — the resolver records LastShownHandle on the
        // widget thread for exactly this read-only consumer.
        var shown = AppState.Instance.ModeState.LastShownHandle;
        var deviceNames = devices.Values.Select(d => d.Name).ToList();
        var rows = devices.Values.Select(d => new Row(
            DeviceLabels.Label(d.Name, d.Handle, deviceNames),
            $"{d.BatteryPercentage}%",
            HistoryService.EstimateFor(d.Handle) is { } e ? HistoryService.FormatEstimateCompact(e) : "",
            d.BatteryPercentage,
            d.IsCharging,
            d.BatterySaver,
            d.IsConnected,
            d.IsConnected && d.Handle == shown,
            d.Kind)).ToList();
        rows.Sort((a, b) =>
        {
            int c = b.Connected.CompareTo(a.Connected);
            return c != 0 ? c : string.CompareOrdinal(a.Name.ToLowerInvariant(), b.Name.ToLowerInvariant());
        });
        return rows;
    }

    private static float ScaleOf()
    {
        uint dpi = _hoverWnd != 0 ? GetDpiForWindow(_hoverWnd) : 96;
        return dpi == 0 ? 1.0f : dpi / 96.0f;
    }

    /// <summary>Column widths shared by Measure and PaintD2D so the layout
    /// math can never diverge between the two passes.</summary>
    private static (int NameW, int PctW, int EtaW, int GlyphW, int KindW, bool AnyEta) Columns(List<Row> rows)
    {
        float scale = ScaleOf();
        int textH = (int)MathF.Round(13.0f * scale);
        int iconH = DeviceIcons.SnapSize((int)MathF.Round(20.0f * scale));
        var textFont = D2d.Format(textH, TextAlignmentLeading, FontWeightSemiBold);
        var iconFont = D2d.Format(iconH, TextAlignmentLeading, FontWeightNormal, icon: true);
        int nameW = 0, pctW = 0, etaW = 0;
        foreach (var r in rows)
        {
            nameW = Math.Max(nameW, D2d.TextWidth(r.Name, textFont));
            pctW = Math.Max(pctW, D2d.TextWidth(r.Pct, textFont));
            etaW = Math.Max(etaW, D2d.TextWidth(r.Eta, textFont));
        }
        bool anyEta = rows.Any(r => r.Eta.Length > 0);
        etaW = anyEta ? etaW : 0;
        int glyphW = Math.Max(D2d.TextWidth("\uE85A", iconFont), 1); // widest battery glyph
        int kindW = rows.Count > 0 ? rows.Max(r => DeviceIcons.WidthFor(iconH, r.Kind)) : 0;
        return (nameW, pctW, etaW, glyphW, kindW, anyEta);
    }

    /// <summary>Panel size for `rows` (measured with the real formats, DPI-scaled).</summary>
    private static (int W, int H) Measure(List<Row> rows)
    {
        if (!D2d.EnsureGraphics())
        {
            return (0, 0);
        }
        float scale = ScaleOf();
        var cols = Columns(rows);
        int pad = (int)MathF.Round(10.0f * scale);
        int gap = (int)MathF.Round(6.0f * scale);
        int textH = (int)MathF.Round(14.0f * scale);
        int iconH = DeviceIcons.SnapSize((int)MathF.Round(15.0f * scale));
        int rowH = Math.Max(iconH, textH);
        int rowGap = (int)MathF.Round(3.0f * scale);
        int n = rows.Count;
        // Columns: [type icon] [glyph] [name] [eta?] [pct].
        int colsW = cols.KindW + gap + cols.GlyphW + gap + cols.NameW + gap;
        if (cols.EtaW > 0)
        {
            colsW += cols.EtaW + gap;
        }
        colsW += cols.PctW;
        int w = Math.Max((pad * 2) + colsW, (int)MathF.Round(150.0f * scale));
        int h = (pad * 2) + (n * rowH) + (Math.Max(n - 1, 0) * rowGap);
        return (w, h);
    }

    /// <summary>Place the panel against the widget: horizontally centered on
    /// the cursor (clamped to the work area), opening upward from a
    /// bottom-docked taskbar, downward from a top-docked one.</summary>
    private static (int X, int Y) Place(IntPtr widget, RECT wrect, int w, int h, POINT cursor)
    {
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        var hmon = MonitorFromWindow(widget, MONITOR_DEFAULTTONEAREST);
        if (hmon == 0 || !GetMonitorInfoW(hmon, ref mi))
        {
            return (cursor.X - w / 2, wrect.Top - h - 4);
        }
        var work = mi.rcWork;
        int x = Math.Clamp(cursor.X - w / 2, work.Left + 4, Math.Max(work.Right - w - 4, work.Left + 4));
        int y = wrect.Top >= (work.Top + work.Bottom) / 2 ? wrect.Top - h - 4 : wrect.Bottom + 4;
        return (x, Math.Max(y, work.Top + 4));
    }

    private static void EnsureWindow()
    {
        if (_hoverWnd != 0)
        {
            return;
        }
        var instance = GetModuleHandleW(null);
        var wc = new WNDCLASSW
        {
            style = CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc = HoverWndProc,
            hInstance = instance,
            lpszClassName = ClassName,
            hCursor = LoadCursorW(0, (IntPtr)IdcArrow),
        };
        RegisterClassW(ref wc); // re-registering fails harmlessly
        var hwnd = CreateWindowExW(
            WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE,
            ClassName, ClassName, WS_POPUP,
            0, 0, 10, 10, 0, 0, instance, 0);
        if (hwnd == 0)
        {
            Log.Error("razer-taskbar: hover panel CreateWindowExW failed");
            return;
        }
        // Per-pixel-alpha layered presentation: SetLayeredWindowAttributes is
        // NOT used here — the panel is presented with UpdateLayeredWindow
        // (see Present), and the two modes are mutually exclusive.
        _hoverWnd = hwnd;
    }

    private static void UpdateAndShow(IntPtr widget, RECT wrect, POINT cursor)
    {
        EnsureWindow();
        if (_hoverWnd == 0)
        {
            return;
        }
        var rows = SnapshotRows();
        float scale = ScaleOf();
        bool rowsChanged = !_rows.SequenceEqual(rows);
        // The size depends only on (rows, DPI): an unchanged hover reuses the
        // last measure instead of re-running the font-measure pass every
        // 120ms tick. Position still follows the cursor.
        bool sizeKnown = !rowsChanged && _panelSize.W > 0 && scale == _lastScale;
        var (w, h) = sizeKnown ? _panelSize : Measure(rows);
        if (w <= 0 || h <= 0)
        {
            return; // graphics unavailable (see D2d.EnsureGraphics)
        }
        var pos = Place(widget, wrect, w, h, cursor);
        bool geometryChanged = _panelPos != pos || _panelSize != (w, h) || !_shown;
        _rows = rows;
        _panelPos = pos;
        _panelSize = (w, h);
        _lastScale = scale;
        _shown = true;
        if (geometryChanged)
        {
            SetWindowPos(_hoverWnd, HwndTopmost, pos.X, pos.Y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            InvalidateRect(_hoverWnd, 0, true);
        }
        else if (rowsChanged)
        {
            InvalidateRect(_hoverWnd, 0, true);
        }
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const long IdcArrow = 32512;

    private static IntPtr HoverWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
                Paint();
                return 0;
            // Click-through like the widget: the panel is read-only info.
            case WM_NCHITTEST:
                return (IntPtr)HTTRANSPARENT;
            default:
                return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    /// <summary>Columns: [type icon] [battery glyph] [name …] [eta] [pct].
    /// Painted into the 32bpp DIB and presented with per-pixel alpha
    /// (UpdateLayeredWindow). D2D writes premultiplied alpha directly, so
    /// content pixels keep real partial coverage — nothing is reclassified
    /// after the fact.</summary>
    private static void Paint()
    {
        var hwnd = _hoverWnd;
        if (hwnd == 0)
        {
            return;
        }
        var hdc = BeginPaint(hwnd, out var ps);
        if (hdc == 0)
        {
            return;
        }
        try
        {
            var rows = _rows;
            var (w, h) = _panelSize;
            EnsureSurface(w, h);
            if (_rt == null || _memDc == 0)
            {
                // No D2D surface this tick — skip rather than draw directly
                // onto the ULW window, whose pixels would be replaced by the
                // next upload anyway.
                return;
            }
            PaintD2D(rows, w, h);
            Present(hwnd);
        }
        catch (Exception e)
        {
            Log.Error("hover panel paint failed", e);
        }
        finally
        {
            EndPaint(hwnd, ref ps);
        }
    }

    private static void PaintD2D(List<Row> rows, int w, int h)
    {
        var rt = _rt!;
        var brush = _brush!;
        var full = new RECT { Left = 0, Top = 0, Right = w, Bottom = h };
        rt.BindDC(_memDc, ref full);
        rt.BeginDraw();
        try
        {
            var transparent = new D2D1_COLOR_F(0f, 0f, 0f, 0f);
            rt.Clear(ref transparent);
            rt.SetAntialiasMode(D2D1Consts.AntialiasModePerPrimitive);
            rt.SetTextAntialiasMode(D2D1Consts.TextAntialiasModeGrayscale);

            float scale = ScaleOf();
            float radius = MathF.Round(8.0f * scale);
            var rect = new D2D1_RECT_F(0f, 0f, w, h);
            // Card base: real 50%-translucent dark rounded rect; corners are
            // anti-aliased by D2D itself (no analytic coverage pass needed).
            var cardColor = D2D1.Color(0x20, 0x20, 0x20, 0.5f);
            brush.SetColor(ref cardColor);
            rt.FillRoundedRectangle(new D2D1_ROUNDED_RECT { Rect = rect, RadiusX = radius, RadiusY = radius }, brush);
            var inset = new D2D1_RECT_F(0.5f, 0.5f, w - 0.5f, h - 0.5f);
            var borderColor = D2D1.Color(0x5A, 0x5A, 0x5A, 1f);
            brush.SetColor(ref borderColor);
            rt.DrawRoundedRectangle(new D2D1_ROUNDED_RECT { Rect = inset, RadiusX = radius, RadiusY = radius }, brush, 1f, IntPtr.Zero);

            int pad = (int)MathF.Round(10.0f * scale);
            int gap = (int)MathF.Round(6.0f * scale);
            int textH = (int)MathF.Round(13.0f * scale);
            int iconH = DeviceIcons.SnapSize((int)MathF.Round(20.0f * scale));
            int rowH = Math.Max(iconH, textH);
            int rowGap = (int)MathF.Round(3.0f * scale);
            var cols = Columns(rows);
            var nameFormat = D2d.Format(textH, TextAlignmentLeading, FontWeightSemiBold, ellipsis: true);
            var rightFormat = D2d.Format(textH, TextAlignmentTrailing, FontWeightSemiBold);
            var glyphFormat = D2d.Format(iconH, TextAlignmentLeading, FontWeightNormal, icon: true);
            var kindFormat = D2d.Format(iconH, TextAlignmentCenter, FontWeightNormal, icon: true);

            // Columns grow leftward from the percentage: predicted time first.
            int pctRight = w - pad;
            int pctLeft = pctRight - cols.PctW;
            int left = pctLeft - gap;
            int etaLeft = 0, etaRight = 0;
            if (cols.AnyEta)
            {
                etaRight = left;
                etaLeft = etaRight - cols.EtaW;
                left = etaLeft - gap;
            }
            int glyphX = pad + cols.KindW + gap;
            int nameLeft = glyphX + cols.GlyphW + gap;
            int nameRight = Math.Max(left, nameLeft);

            // Coloring: the charging/saver/disconnected state colors always
            // tint glyph layer 1 and the pct; Settings → Colored battery
            // icon gates only the plain-discharge level gradient (off:
            // those rows draw the default white glyph/pct).
            bool colorize = AppState.Instance.ConfigSnapshot().ColorBatteryIcon;

            int y = pad;
            foreach (var r in rows)
            {
                int top = y;
                int bottom = y + rowH;
                var c = BatteryColors.LevelFillColor(r.Level, r.Connected, r.Saver, r.Charging, colorize);
                var levelColor = D2D1.Color(c.R, c.G, c.B, 1f);

                // Device-type icon: light gray for connected, dim for not;
                // centered in the kind column by the text format.
                byte kc = r.Connected ? (byte)0xE8 : (byte)0x78;
                Tint(brush, kc, kc, kc);
                var kindRect = new D2D1_RECT_F(pad, top, pad + cols.KindW, bottom);
                rt.DrawText(DeviceIcons.GlyphFor(r.Kind).ToString(), 1, kindFormat, ref kindRect, brush,
                    D2D1Consts.DrawTextOptionsNone, MeasuringModeNatural);

                // Battery glyph, two layers: the ACTIVE series' level glyph
                // tinted with the state color, then the same series' 0%
                // glyph (plain outline, or outline + bolt/leaf) on top in
                // the default color, masking the tinted outline and symbol
                // so only the fill stays colored. Same series is required:
                // the status glyphs cut their outline where the symbol
                // crosses it.
                var glyphRect = new D2D1_RECT_F(glyphX, top, glyphX + cols.GlyphW + 4, bottom);
                Tint(brush, c.R, c.G, c.B);
                rt.DrawText(BatteryGlyphs.LevelGlyph(r.Level, r.Connected, r.Saver, r.Charging).ToString(),
                    1, glyphFormat, ref glyphRect, brush,
                    D2D1Consts.DrawTextOptionsNone, MeasuringModeNatural);
                if (BatteryGlyphs.StatusOverlayGlyph(r.Connected, r.Saver, r.Charging) is { } statusGlyph)
                {
                    Tint(brush, 0xFF, 0xFF, 0xFF);
                    rt.DrawText(statusGlyph.ToString(), 1, glyphFormat, ref glyphRect, brush,
                        D2D1Consts.DrawTextOptionsNone, MeasuringModeNatural);
                }

                // Name: displayed device bright white, connected dimmer,
                // disconnected gray.
                byte nc = r.Displayed ? (byte)0xFF : r.Connected ? (byte)0xD8 : (byte)0x90;
                if (r.Name.Length > 0)
                {
                    Tint(brush, nc, nc, nc);
                    var nameRect = new D2D1_RECT_F(nameLeft, top, Math.Max(nameRight, nameLeft), bottom);
                    rt.DrawText(r.Name, (uint)r.Name.Length, nameFormat, ref nameRect, brush,
                        D2D1Consts.DrawTextOptionsNone, MeasuringModeNatural);
                }
                // Predicted usage time / time-to-full (right-aligned, dim).
                if (cols.AnyEta && r.Eta.Length > 0)
                {
                    Tint(brush, 0xB0, 0xB0, 0xB0);
                    var etaRect = new D2D1_RECT_F(etaLeft - 2, top, etaRight, bottom);
                    rt.DrawText(r.Eta, (uint)r.Eta.Length, rightFormat, ref etaRect, brush,
                        D2D1Consts.DrawTextOptionsNone, MeasuringModeNatural);
                }
                // Percentage, state/level-colored, right-aligned.
                Tint(brush, c.R, c.G, c.B);
                var pctRect = new D2D1_RECT_F(pctLeft - 2, top, pctRight, bottom);
                rt.DrawText(r.Pct, (uint)r.Pct.Length, rightFormat, ref pctRect, brush,
                    D2D1Consts.DrawTextOptionsNone, MeasuringModeNatural);
                y += rowH + rowGap;
            }
        }
        finally
        {
            var hr = rt.EndDraw(out _, out _);
            if (hr != 0)
            {
                Log.Error($"razer-taskbar: hover EndDraw failed: 0x{hr:X8}");
                if ((uint)hr == 0x88990010u)
                {
                    DestroySurface(); // D2DERR_RECREATE_TARGET — rebuilt next tick
                }
            }
        }
    }

    private static void Tint(ID2D1SolidColorBrush b, byte r, byte g, byte bl)
    {
        var c = D2D1.Color(r, g, bl, 1f);
        b.SetColor(ref c);
    }

    /// <summary>Offscreen 32bpp top-down DIB + the DC render target bound to
    /// it; recreated only when the size changes. Widget-thread only.</summary>
    private static void EnsureSurface(int w, int h)
    {
        if (_memDc != 0 && _rt != null && _memW == w && _memH == h)
        {
            return;
        }
        if (!D2d.EnsureGraphics())
        {
            return;
        }
        DestroySurface();
        var wdc = GetDC(_hoverWnd);
        if (wdc == 0)
        {
            return;
        }
        _memDc = CreateCompatibleDC(wdc);
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            },
        };
        _memBmp = CreateDIBSection(wdc, ref bmi, DIB_RGB_COLORS, out _memBits, 0, 0);
        ReleaseDC(_hoverWnd, wdc);
        if (_memDc == 0 || _memBmp == 0 || _memBits == 0)
        {
            DestroySurface();
            return;
        }
        SelectObject(_memDc, _memBmp);
        _memW = w;
        _memH = h;
        try
        {
            _rt = D2d.CreateRenderTarget();
            var full = new RECT { Left = 0, Top = 0, Right = w, Bottom = h };
            _rt.BindDC(_memDc, ref full);
            _brush = D2d.CreateBrush(_rt);
        }
        catch (Exception e)
        {
            Log.Error("razer-taskbar: hover D2D render target creation failed", e);
            DestroySurface();
        }
    }

    private static void DestroySurface()
    {
        D2d.TryRelease(_brush);
        _brush = null;
        D2d.TryRelease(_rt);
        _rt = null;
        if (_memDc != 0)
        {
            DeleteDC(_memDc);
            _memDc = 0;
        }
        if (_memBmp != 0)
        {
            DeleteObject(_memBmp);
            _memBmp = 0;
        }
        _memBits = 0;
        _memW = 0;
        _memH = 0;
    }

    /// <summary>Hand the painted DIB frame to DWM via UpdateLayeredWindow.
    /// The D2D frame already carries correct premultiplied alpha — nothing
    /// to recompute here.</summary>
    private static void Present(IntPtr hwnd)
    {
        var dst = new POINT();
        if (TaskbarLocator.WindowRect(hwnd) is { } wr)
        {
            dst.X = wr.Left;
            dst.Y = wr.Top;
        }
        var size = new SIZE(_memW, _memH);
        var src = new POINT();
        var blend = new BLENDFUNCTION
        {
            BlendOp = 0, // AC_SRC_OVER
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = AC_SRC_ALPHA,
        };
        if (!UpdateLayeredWindow(hwnd, 0, ref dst, ref size, _memDc, ref src, 0, ref blend, ULW_ALPHA))
        {
            // Log at most the first failure; a silent blank popup is worse.
            if (!_ulwFailLogged)
            {
                _ulwFailLogged = true;
                Log.Error($"razer-taskbar: hover UpdateLayeredWindow failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            }
        }
    }
}
