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
    private static IntPtr _hoverWnd;

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
        UpdateAndShow(widget, wr);
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
        var shown = DeviceSelector.PickDeviceToDisplay(devices)?.Handle ?? "";
        var rows = devices.Values.Select(d => new Row(
            d.Name,
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

    private static IntPtr CreateIconFont(int h)
    {
        // Snap to Microsoft's magic icon sizes (16/20/24/…) for crisp glyphs.
        h = DeviceIcons.SnapSize(h);
        var f = CreateFontW(h, 0, 0, 0, FW_NORMAL, 0, 0, 0,
            DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY,
            DEFAULT_PITCH | FF_DONTCARE, "Segoe Fluent Icons");
        // Fallback for Win10 (no Fluent Icons font).
        if (f == 0)
        {
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
        if (f == 0)
        {
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
        var textFont = CreateTextFont(textH);
        var iconFont = CreateIconFont(iconH);
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
        DeleteObject(textFont);
        DeleteObject(iconFont);
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

    /// <summary>Place the panel against the widget: open upward from a
    /// bottom-docked taskbar, downward from a top-docked one.</summary>
    private static (int X, int Y) Place(IntPtr widget, RECT wrect, int w, int h)
    {
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        var hmon = MonitorFromWindow(widget, MONITOR_DEFAULTTONEAREST);
        if (hmon == 0 || !GetMonitorInfoW(hmon, ref mi))
        {
            return (wrect.Left, wrect.Top - h - 4);
        }
        var work = mi.rcWork;
        int x = Math.Clamp(wrect.Left, work.Left + 4, Math.Max(work.Right - w - 4, work.Left + 4));
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
            Console.Error.WriteLine("razer-taskbar: hover panel CreateWindowExW failed");
            return;
        }
        // Same single-call colorkey rule as the widget.
        SetLayeredWindowAttributes(hwnd, 0x00000000, 0, LWA_COLORKEY);
        _hoverWnd = hwnd;
    }

    private static void UpdateAndShow(IntPtr widget, RECT wrect)
    {
        EnsureWindow();
        if (_hoverWnd == 0)
        {
            return;
        }
        var rows = SnapshotRows();
        var (w, h) = Measure(rows);
        var pos = Place(widget, wrect, w, h);
        bool rowsChanged = !_rows.SequenceEqual(rows);
        bool geometryChanged = _panelPos != pos || _panelSize != (w, h) || !_shown;
        _rows = rows;
        _panelPos = pos;
        _panelSize = (w, h);
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
    /// Opaque dark panel; the black colorkey only cuts the outer margin.</summary>
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
            // Rounded dark panel: one RoundRect fills the body and strokes the
            // border; the square corners stay color-key black → transparent.
            float scale = ScaleOf();
            int radius = (int)MathF.Round(8.0f * scale);
            var bg = CreateSolidBrush(0x00202020);
            var border = CreatePen(PS_SOLID, 1, 0x005A5A5A);
            var oldPen = SelectObject(hdc, border);
            var oldBrush = SelectObject(hdc, bg);
            RoundRect(hdc, 0, 0, w, h, radius * 2, radius * 2);
            SelectObject(hdc, oldPen);
            SelectObject(hdc, oldBrush);
            DeleteObject(bg);
            DeleteObject(border);
            SetBkMode(hdc, TRANSPARENT);

            int pad = (int)MathF.Round(10.0f * scale);
            int gap = (int)MathF.Round(6.0f * scale);
            int textH = (int)MathF.Round(14.0f * scale);
            int iconH = DeviceIcons.SnapSize((int)MathF.Round(15.0f * scale));
            int rowH = Math.Max(iconH, textH);
            int rowGap = (int)MathF.Round(3.0f * scale);
            var iconFont = CreateIconFont(iconH);
            var textFont = CreateTextFont(textH);
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

            int y = pad;
            foreach (var r in rows)
            {
                int top = y;
                int bottom = y + rowH;
                var c = r.Connected ? BatteryColors.ColorFor(r.Level) : new RgbColor(0x80, 0x80, 0x80);
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
                // Battery glyph: per-state Win11 series, level-tinted.
                var state = !r.Connected ? BatteryGlyphState.Normal
                    : r.Saver ? BatteryGlyphState.Saver
                    : r.Charging ? BatteryGlyphState.Charging
                    : BatteryGlyphState.Normal;
                var glyph = new[] { BatteryGlyphs.BatteryGlyph(r.Level, state) };
                var grc = new RECT { Left = glyphX, Top = top, Right = glyphX + glyphW + 4, Bottom = bottom };
                SelectObject(hdc, iconFont);
                SetTextColor(hdc, levelColor);
                DrawTextW(hdc, glyph, glyph.Length, ref grc, DT_SINGLELINE | DT_VCENTER | DT_LEFT);
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
                // Percentage, level-colored, right-aligned.
                var pct = r.Pct.ToCharArray();
                var prc = new RECT { Left = pctLeft - 2, Top = top, Right = pctRight, Bottom = bottom };
                SelectObject(hdc, textFont);
                SetTextColor(hdc, levelColor);
                DrawTextW(hdc, pct, pct.Length, ref prc, DT_SINGLELINE | DT_VCENTER | DT_RIGHT);
                y += rowH + rowGap;
            }
            SelectObject(hdc, old);
            DeleteObject(iconFont);
            DeleteObject(textFont);
        }
        finally
        {
            EndPaint(hwnd, ref ps);
        }
    }
}
