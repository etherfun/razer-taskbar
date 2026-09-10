//! Port of src/hover.rs: hover popover listing every known Razer device
//! (name, battery, charging). The widget overlay is fully click-through, so
//! hover is detected by polling the cursor against the widget rect on the
//! fast TIMER_HOVER tick (120ms). The panel is a topmost layered NOACTIVATE
//! window that never takes focus and is itself click-through.

using System.Diagnostics;
using System.Runtime.InteropServices;
using RazerTaskbar.Core;
using static RazerTaskbar.Native.Interop.Gdi32;
using RazerTaskbar.Native.Interop;
using static RazerTaskbar.Native.Interop.User32;
using static RazerTaskbar.Native.Interop.Win32Consts;

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
    // upload (see EnsureSurface / Present).
    private static IntPtr _memDc;
    private static IntPtr _memBmp;
    private static IntPtr _memBits;
    private static int _memW;
    private static int _memH;
    private static byte[] _buf = Array.Empty<byte>();
    private static bool _ulwFailLogged;

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
        foreach (var f in IconFontCache.Values)
        {
            DeleteObject(f);
        }
        foreach (var f in TextFontCache.Values)
        {
            DeleteObject(f);
        }
        IconFontCache.Clear();
        TextFontCache.Clear();
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

    // Font caches, widget-thread only (same pattern as GdiText.CachedTextFont).
    // Every Measure tick and every WM_PAINT used to create and delete a pair
    // of fonts; heights are DPI-snapped so the key space stays tiny. Handles
    // are shared — callers SelectObject but never delete.
    private static readonly Dictionary<int, IntPtr> IconFontCache = new();
    private static readonly Dictionary<int, IntPtr> TextFontCache = new();

    private static IntPtr CachedIconFont(int h)
    {
        if (!IconFontCache.TryGetValue(h, out var f))
        {
            IconFontCache[h] = f = CreateIconFont(h);
        }
        return f;
    }

    private static IntPtr CachedPanelTextFont(int h)
    {
        if (!TextFontCache.TryGetValue(h, out var f))
        {
            TextFontCache[h] = f = CreateTextFont(h);
        }
        return f;
    }

    private static IntPtr CreateIconFont(int h)
    {
        // Snap to Microsoft's magic icon sizes (16/20/24/…) for crisp glyphs.
        h = DeviceIcons.SnapSize(h);
        var f = CreateFontW(h, 0, 0, 0, FW_NORMAL, 0, 0, 0,
            DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY,
            DEFAULT_PITCH | FF_DONTCARE, "Segoe Fluent Icons");
        // Face-verified fallback for Win10 (no Fluent Icons font): a
        // nonzero handle proves nothing — CreateFontW silently substitutes
        // unknown faces (see GdiText.CreateTextFont).
        if (!GdiText.FaceResolved(f, "Segoe Fluent Icons"))
        {
            DeleteObject(f);
            f = CreateFontW(h, 0, 0, 0, FW_NORMAL, 0, 0, 0,
                DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY,
                DEFAULT_PITCH | FF_DONTCARE, "Segoe MDL2 Assets");
        }
        return f;
    }

    private static IntPtr CreateTextFont(int h)
    {
        var f = CreateFontW(h, 0, 0, 0, FW_SEMIBOLD, 0, 0, 0,
            DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY,
            DEFAULT_PITCH | FF_DONTCARE, "Segoe UI Variable Text");
        if (!GdiText.FaceResolved(f, "Segoe UI Variable Text"))
        {
            DeleteObject(f);
            f = CreateFontW(h, 0, 0, 0, FW_SEMIBOLD, 0, 0, 0,
                DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY,
                DEFAULT_PITCH | FF_DONTCARE, "Segoe UI");
        }
        return f;
    }

    /// <summary>Panel size for `rows` (measured with the real fonts, DPI-scaled).</summary>
    private static (int W, int H) Measure(List<Row> rows)
    {
        float scale = ScaleOf();
        var hdc = GetDC(0);
        int textH = (int)MathF.Round(14.0f * scale);
        int iconH = DeviceIcons.SnapSize((int)MathF.Round(15.0f * scale));
        var textFont = CachedPanelTextFont(textH);
        var iconFont = CachedIconFont(iconH);
        var old = SelectObject(hdc, textFont);
        int nameW = 0, pctW = 0, etaW = 0;
        foreach (var r in rows)
        {
            nameW = Math.Max(nameW, GdiText.TextWidth(hdc, r.Name));
            pctW = Math.Max(pctW, GdiText.TextWidth(hdc, r.Pct));
            etaW = Math.Max(etaW, GdiText.TextWidth(hdc, r.Eta));
        }
        bool anyEta = rows.Any(r => r.Eta.Length > 0);
        etaW = anyEta ? etaW : 0;
        SelectObject(hdc, iconFont);
        int glyphW = GdiText.TextWidth(hdc, "\uE85A"); // widest battery glyph
        SelectObject(hdc, old);
        // Device-type icon column: widest kind at this row height (must run
        // while hdc is still valid).
        int kindW = rows.Count > 0 ? rows.Max(r => DeviceIcons.WidthFor(hdc, iconH, r.Kind)) : 0;
        ReleaseDC(0, hdc);
        int pad = (int)MathF.Round(10.0f * scale);
        int gap = (int)MathF.Round(6.0f * scale);
        int rowH = Math.Max(iconH, textH);
        int rowGap = (int)MathF.Round(3.0f * scale);
        int n = rows.Count;
        // Columns: [type icon] [glyph] [name] [eta?] [pct].
        int cols = kindW + gap + glyphW + gap + nameW + gap;
        if (etaW > 0)
        {
            cols += etaW + gap;
        }
        cols += pctW;
        int w = Math.Max((pad * 2) + cols, (int)MathF.Round(150.0f * scale));
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
    /// Painted into a 32bpp DIB and presented with per-pixel alpha
    /// (UpdateLayeredWindow, same technique as the widget): content pixels
    /// carry full alpha and always render, the card body is 50% translucent,
    /// and corners are anti-aliased by the presenter.</summary>
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
            if (_memDc == 0)
            {
                // No offscreen surface this tick — skip rather than draw
                // straight onto the ULW window, whose pixels would be
                // replaced by the next upload anyway.
                return;
            }
            PaintBody(_memDc, rows, w, h);
            Present(hwnd);
        }
        finally
        {
            EndPaint(hwnd, ref ps);
        }
    }

    private static void PaintBody(IntPtr hdc, List<Row> rows, int w, int h)
    {
        try
        {
            // Card base: opaque dark fill. The presenter turns bare-card
            // pixels 50%-translucent (dimmed backdrop shows through) and
            // content pixels fully opaque, with anti-aliased corners.
            float scale = ScaleOf();
            int radius = (int)MathF.Round(8.0f * scale);
            var bg = CreateSolidBrush(0x00202020);
            var keyRect = new RECT { Left = 0, Top = 0, Right = w, Bottom = h };
            FillRect(hdc, ref keyRect, bg);
            DeleteObject(bg);
            var border = CreatePen(PS_SOLID, 1, 0x005A5A5A);
            var oldPen = SelectObject(hdc, border);
            var oldBrush = SelectObject(hdc, GetStockObject(NULL_BRUSH));
            RoundRect(hdc, 0, 0, w, h, radius * 2, radius * 2);
            SelectObject(hdc, oldPen);
            SelectObject(hdc, oldBrush);
            DeleteObject(border);
            SetBkMode(hdc, TRANSPARENT);

            int pad = (int)MathF.Round(10.0f * scale);
            int gap = (int)MathF.Round(6.0f * scale);
            int textH = (int)MathF.Round(14.0f * scale);
            int iconH = DeviceIcons.SnapSize((int)MathF.Round(15.0f * scale));
            int rowH = Math.Max(iconH, textH);
            int rowGap = (int)MathF.Round(3.0f * scale);
            var iconFont = CachedIconFont(iconH);
            var textFont = CachedPanelTextFont(textH);
            var old = SelectObject(hdc, textFont);

            int pctW = 0, etaW = 0;
            foreach (var r in rows)
            {
                pctW = Math.Max(pctW, GdiText.TextWidth(hdc, r.Pct));
                etaW = Math.Max(etaW, GdiText.TextWidth(hdc, r.Eta));
            }
            bool anyEta = rows.Any(r => r.Eta.Length > 0);
            etaW = anyEta ? etaW : 0;
            SelectObject(hdc, iconFont);
            int glyphW = GdiText.TextWidth(hdc, "\uE85A");
            SelectObject(hdc, old);

            // Columns: [type icon] [battery glyph] [name …] [eta?] [pct].
            int kindW = rows.Count > 0 ? rows.Max(r => DeviceIcons.WidthFor(hdc, iconH, r.Kind)) : 0;
            int pctRight = w - pad;
            int pctLeft = pctRight - pctW;
            // Columns grow leftward from the percentage: predicted time first.
            int left = pctLeft - gap;
            int etaLeft = 0, etaRight = 0;
            if (anyEta)
            {
                etaRight = left;
                etaLeft = etaRight - etaW;
                left = etaLeft - gap;
            }
            int glyphX = pad + kindW + gap;
            int nameLeft = glyphX + glyphW + gap;
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
                uint levelColor = GdiText.ColorRef(c.R, c.G, c.B);
                // Device-type icon: light gray for connected, dim for not;
                // centered in the kind column and the row.
                var kindRgb = r.Connected ? ((byte)0xE8, (byte)0xE8, (byte)0xE8) : ((byte)0x78, (byte)0x78, (byte)0x78);
                int ibh = Math.Min(rowH, iconH);
                int iw = DeviceIcons.WidthFor(hdc, ibh, r.Kind);
                DeviceIcons.Draw(
                    hdc,
                    // Round the leftover half-pixel up (mouse glyph centering).
                    pad + ((kindW - iw + 1) / 2),
                    top + ((rowH - ibh) / 2),
                    ibh,
                    r.Kind,
                    kindRgb);
                // Battery glyph, two layers: the ACTIVE series' level glyph
                // tinted with the state color, then the same series' 0%
                // glyph (plain outline, or outline + bolt/leaf) on top in
                // the default color, masking the tinted outline and symbol
                // so only the fill stays colored. Same series is required:
                // the status glyphs cut their outline where the symbol
                // crosses it.
                var glyph = new[] { BatteryGlyphs.LevelGlyph(r.Level, r.Connected, r.Saver, r.Charging) };
                var grc = new RECT { Left = glyphX, Top = top, Right = glyphX + glyphW + 4, Bottom = bottom };
                SelectObject(hdc, iconFont);
                // Layer 1: levelColor — the state colors always, the level
                // gradient only while the option is on (see LevelFillColor;
                // off + plain discharge is the default white).
                SetTextColor(hdc, levelColor);
                DrawTextW(hdc, glyph, glyph.Length, ref grc, DT_SINGLELINE | DT_VCENTER | DT_LEFT);
                if (BatteryGlyphs.StatusOverlayGlyph(r.Connected, r.Saver, r.Charging) is { } statusGlyph)
                {
                    var status = new[] { statusGlyph };
                    var src = new RECT { Left = glyphX, Top = top, Right = glyphX + glyphW + 4, Bottom = bottom };
                    SetTextColor(hdc, 0x00FF_FFFFu);
                    DrawTextW(hdc, status, status.Length, ref src, DT_SINGLELINE | DT_VCENTER | DT_LEFT);
                }
                // Name: displayed device bright white, connected dimmer,
                // disconnected gray.
                uint nameColor = r.Displayed ? 0x00FF_FFFFu
                    : r.Connected ? 0x00D8D8D8u
                    : 0x00909090u;
                if (r.Name.Length > 0)
                {
                    var name = r.Name.ToCharArray();
                    var nrc = new RECT { Left = nameLeft, Top = top, Right = Math.Max(nameRight, nameLeft), Bottom = bottom };
                    SelectObject(hdc, textFont);
                    SetTextColor(hdc, nameColor);
                    DrawTextW(hdc, name, name.Length, ref nrc, DT_SINGLELINE | DT_VCENTER | DT_LEFT | DT_END_ELLIPSIS);
                }
                // Predicted usage time / time-to-full (right-aligned, dim).
                if (anyEta && r.Eta.Length > 0)
                {
                    var eta = r.Eta.ToCharArray();
                    var erc = new RECT { Left = etaLeft - 2, Top = top, Right = etaRight, Bottom = bottom };
                    SelectObject(hdc, textFont);
                    SetTextColor(hdc, 0x00B0B0B0u);
                    DrawTextW(hdc, eta, eta.Length, ref erc, DT_SINGLELINE | DT_VCENTER | DT_RIGHT);
                }
                // Percentage, state/level-colored, right-aligned.
                var pct = r.Pct.ToCharArray();
                var prc = new RECT { Left = pctLeft - 2, Top = top, Right = pctRight, Bottom = bottom };
                SelectObject(hdc, textFont);
                SetTextColor(hdc, levelColor);
                DrawTextW(hdc, pct, pct.Length, ref prc, DT_SINGLELINE | DT_VCENTER | DT_RIGHT);
                y += rowH + rowGap;
            }
            SelectObject(hdc, old);
        }
        catch (Exception e)
        {
            Log.Error("hover panel paint body failed", e);
        }
    }

    /// <summary>Offscreen 32bpp top-down DIB matching the panel size;
    /// recreated only when the size changes. Widget-thread only.</summary>
    private static void EnsureSurface(int w, int h)
    {
        if (_memDc != 0 && _memW == w && _memH == h)
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
    }

    private static void DestroySurface()
    {
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

    /// <summary>Turn the GDI render into premultiplied ARGB and upload.
    /// Analytic rounded-rect coverage zeroes the outside and anti-aliases the
    /// corners; bare-card pixels (exact body fill) become 50%-translucent so
    /// the DWM blur-behind shows through, while drawn content — text, glyphs,
    /// border — keeps full alpha and always renders.</summary>
    private static void Present(IntPtr hwnd)
    {
        int bytes = _memW * _memH * 4;
        if (_buf.Length != bytes)
        {
            _buf = new byte[bytes];
        }
        System.Runtime.InteropServices.Marshal.Copy(_memBits, _buf, 0, bytes);
        float scale = ScaleOf();
        int radius = (int)MathF.Round(8.0f * scale);
        float cx = (_memW - 1) / 2f, cy = (_memH - 1) / 2f;
        float innerX = _memW / 2f - radius, innerY = _memH / 2f - radius;
        for (int y = 0; y < _memH; y++)
        {
            float dy = MathF.Abs(y - cy) - innerY;
            if (dy < 0)
            {
                dy = 0;
            }
            for (int x = 0; x < _memW; x++)
            {
                int i = (y * _memW + x) * 4;
                float dx = MathF.Abs(x - cx) - innerX;
                if (dx < 0)
                {
                    dx = 0;
                }
                float d = MathF.Sqrt(dx * dx + dy * dy);
                float cov = Math.Clamp(radius - d + 0.5f, 0f, 1f);
                if (cov <= 0)
                {
                    _buf[i] = 0;
                    _buf[i + 1] = 0;
                    _buf[i + 2] = 0;
                    _buf[i + 3] = 0;
                    continue;
                }
                if (_buf[i] == 0x20 && _buf[i + 1] == 0x20 && _buf[i + 2] == 0x20)
                {
                    // Bare card: 50% translucent dark over the blur.
                    _buf[i] = 0x10;
                    _buf[i + 1] = 0x10;
                    _buf[i + 2] = 0x10;
                    _buf[i + 3] = (byte)MathF.Round(0x80 * cov);
                }
                else
                {
                    // Content: opaque, premultiplied by the corner coverage.
                    float k = cov;
                    _buf[i] = (byte)MathF.Round(_buf[i] * k);
                    _buf[i + 1] = (byte)MathF.Round(_buf[i + 1] * k);
                    _buf[i + 2] = (byte)MathF.Round(_buf[i + 2] * k);
                    _buf[i + 3] = (byte)MathF.Round(255f * k);
                }
            }
        }
        System.Runtime.InteropServices.Marshal.Copy(_buf, 0, _memBits, bytes);

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
