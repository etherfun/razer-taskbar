//! Port of src/tray.rs: notification-area fallback icon. The taskbar hook is
//! primary; the tray icon keeps the app usable when the hook is occluded.
//! Right-clicking opens the same menu as the widget, double-clicking opens
//! the battery history window — both via the WM_TRAY callback.
//!
//! C# extension (no Rust equivalent): `tray_display_mode` styles the icon as
//! either the original battery glyph or the percentage as digits. The style is
//! part of both the Refresh dedupe signature and the HICON cache key, so a
//! Settings toggle repaints on the next tick instead of keeping the old icon.

using System.Runtime.InteropServices;
using RazerTaskbar.Core;
using static RazerTaskbar.Native.Interop.Gdi32;
using static RazerTaskbar.Native.Interop.Shell32;
using RazerTaskbar.Native.Interop;
using static RazerTaskbar.Native.Interop.User32;
using static RazerTaskbar.Native.Interop.Win32Consts;

namespace RazerTaskbar.Native;

public static class TrayIcon
{
    public const uint TrayUid = 1;
    public const uint WmTray = WM_APP + 1;

    /// <summary>Digit face of the numeric style. Segoe UI's digits are too wide
    /// for a 16px tile (two digits cap out at ~0.55 of the icon height before
    /// the advance width runs out), so the percentage uses Bahnschrift's
    /// semi-condensed instance — a Windows 10 1709+ system font with DIN-like
    /// lining figures that are ~15% narrower, which buys enough height to read
    /// the number at a glance. A missing face substitutes silently (GDI), and
    /// the measurement below adapts to whatever actually renders.</summary>
    private const string NumberFontFace = "Bahnschrift SemiCondensed";

    private static IntPtr _hwnd;

    /// <summary>Last displayed (tooltip, render key): refresh() runs every
    /// second and a NIM_MODIFY is only worth doing when something changed.
    /// The key carries the display style as well, otherwise switching
    /// icon/number would dedupe away the repaint.</summary>
    private static (string Tip, IconKey Key)? _last;

    /// <summary>Per-state HICON cache. The tooltip carries a minute-resolution
    /// countdown, so Refresh() swaps the icon about every minute — building a
    /// fresh HICON per swap leaked ~1400 handles a day (each two DIBs). The
    /// cache bounds it to one icon per rendering state (style + level +
    /// state flags); the key space is fixed, entries stay alive while shown
    /// and are all destroyed in Destroy().</summary>
    private static readonly Dictionary<IconKey, IntPtr> Icons = new();

    /// <summary>Bounding cap for <see cref="Icons"/>. The key space is fixed
    /// but wide (two styles x 101 levels x charging/connected/saver), and the
    /// shell copies the image at NIM_ADD/NIM_MODIFY time, so an icon the tray
    /// currently shows is safe to release. Clearing at the cap keeps the GDI
    /// object count flat across a long uptime instead of betting the user never
    /// visits every state (each icon owns two DIBs).</summary>
    private const int IconsMax = 512;

    /// <summary>Everything the rendered pixels depend on. The style is part of
    /// it: the same battery level is a different image in number mode.</summary>
    private readonly record struct IconKey(
        TrayDisplayMode Mode, int Level, bool Charging, bool Connected, bool Saver);

    /// <summary>Cache/dedupe key of a render state. Icon mode draws from
    /// (level, charging, connected) — saver is normalized away because the
    /// glyph has no leaf variant — so its key space stays small; number mode
    /// tints by the full state (connected/saver/charging).</summary>
    private static IconKey KeyFor(TrayDisplayMode mode, TrayIconState? state) => state is { } s
        ? mode == TrayDisplayMode.Number
            ? new IconKey(mode, s.Level, s.Charging, s.Connected, s.Saver)
            : new IconKey(mode, s.Level, s.Charging, s.Connected, false)
        : new IconKey(mode, -1, false, false, false);

    /// <summary>Cached icon for a display state; 0 on GDI failure (not cached,
    /// retried next refresh). Hits the <see cref="IconsMax"/> cap by dropping
    /// the whole cache — the key set is fixed, so this cannot thrash.</summary>
    private static IntPtr CachedIcon(TrayDisplayMode mode, TrayIconState? state)
    {
        var key = KeyFor(mode, state);
        if (!Icons.TryGetValue(key, out var icon) && (icon = BuildIcon(mode, state)) != 0)
        {
            if (Icons.Count >= IconsMax)
            {
                foreach (var cached in Icons.Values)
                {
                    DestroyIcon(cached);
                }
                Icons.Clear();
            }
            Icons[key] = icon;
        }
        return icon;
    }

    /// <summary>Tray icon dimensions: SM_CXSMICON (DPI-scaled for a
    /// PerMonitorV2 process), with the historical 16px as a fallback when the
    /// metric is unavailable.</summary>
    private static int IconSize()
    {
        int size = GetSystemMetrics(SM_CXSMICON);
        return size is >= 8 and <= 256 ? size : 16;
    }

    /// <summary>Create the tray icon (idempotent). `hwnd` receives WM_TRAY.
    /// Also the re-add path after an explorer restart: NIM_ADD with the same
    /// hWnd/uID replaces the registration, never duplicates it.</summary>
    public static void EnsureCreated(IntPtr hwnd)
    {
        _hwnd = hwnd;
        // Fresh registration: reset the dedup so the trailing refresh()
        // really re-applies icon + tooltip on the (new) taskbar.
        _last = null;
        var mode = TrayDisplay.Parse(AppState.Instance.ConfigSnapshot().TrayDisplayMode);
        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = hwnd,
            uID = TrayUid,
            uFlags = NIF_MESSAGE | NIF_TIP | NIF_ICON,
            uCallbackMessage = WmTray,
            szTip = "Razer Taskbar",
            hIcon = CachedIcon(mode, null),
        };
        Shell_NotifyIconW(NIM_ADD, ref nid);
        // Deliberately NOT NOTIFYICON_VERSION_4: v4 suppresses the standard
        // szTip tooltip (unless NIF_SHOWTIP is also set) and delivers
        // WM_CONTEXTMENU/NIN_SELECT instead of the mouse messages the WM_TRAY
        // dispatch in WidgetWindow handles. Legacy keeps tooltip + events
        // working with zero extra plumbing.
        Refresh();
    }

    /// <summary>Rebuild icon + tooltip from the current device state (deduped).</summary>
    public static void Refresh()
    {
        if (_hwnd == 0)
        {
            return;
        }
        var cfg = AppState.Instance.ConfigSnapshot();
        var mode = TrayDisplay.Parse(cfg.TrayDisplayMode);
        string tip;
        TrayIconState? state;
        var device = DisplayModeResolver.Pick(
            AppState.Instance.Devices.Snapshot(), cfg,
            AppState.Instance.ModeState, Environment.TickCount64);
        if (device is { } d)
        {
            // `keep_recorded_devices` can hand us a recorded device that is not
            // online: keep its last reading but mark it offline — and no usage
            // estimate (predictions need a live discharge anchor).
            var charging = d.IsCharging && d.IsConnected ? $" {I18n.Tr("(charging)")}" : "";
            var offline = d.IsConnected ? "" : $" {I18n.Tr("offline")}";
            tip = $"{d.Name}: {d.BatteryPercentage}%{charging}{offline}";
            // Predicted usage time / time-to-full from history.
            if (d.IsConnected && HistoryService.EstimateFor(d.Handle) is { } e)
            {
                tip += $" · {HistoryService.FormatEstimateVerbose(e)}";
            }
            state = new TrayIconState(d.BatteryPercentage, d.IsCharging, d.IsConnected, d.BatterySaver);
        }
        else
        {
            tip = I18n.Tr("No devices found");
            state = null;
        }
        var key = KeyFor(mode, state);
        if (_last is { } last && last.Tip == tip && last.Key == key)
        {
            return;
        }
        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = TrayUid,
            uFlags = NIF_TIP | NIF_ICON,
            szTip = tip,
            hIcon = CachedIcon(mode, state),
        };
        Shell_NotifyIconW(NIM_MODIFY, ref nid);
        _last = (tip, key);
    }

    public static void Destroy()
    {
        if (_hwnd == 0)
        {
            return;
        }
        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = TrayUid,
        };
        Shell_NotifyIconW(NIM_DELETE, ref nid);
        _hwnd = 0;
        _last = null;
        foreach (var icon in Icons.Values)
        {
            DestroyIcon(icon);
        }
        Icons.Clear();
    }

    /// <summary>Settings-page toggle: create or remove the icon.</summary>
    public static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            var host = WidgetWindow.TrayHostHwnd();
            if (_hwnd == 0 && host != 0)
            {
                EnsureCreated(host);
            }
            else
            {
                Refresh();
            }
        }
        else
        {
            Destroy();
        }
    }

    /// <summary>Build the HICON for a display state. A nonzero result is owned
    /// by the <see cref="Icons"/> cache (the shell copies the image at
    /// NIM_ADD/NIM_MODIFY time) and lives until Destroy(); 0 on GDI failure
    /// (handle exhaustion): the caller's NIM_ADD/NIM_MODIFY then degrades to an
    /// icon-less registration, same silent-failure rule as the other DIB sites
    /// (WidgetWindow/HoverPanel/D2d scanner).</summary>
    private static IntPtr BuildIcon(TrayDisplayMode mode, TrayIconState? state)
    {
        int size = IconSize();
        return RenderPixels(mode, state, size) is { } pixels ? CreateIconFromPixels(pixels, size) : 0;
    }

    /// <summary>Compose straight-alpha ARGB pixels (0xAARRGGBB) of the tray
    /// icon at `size`x`size`. Drawn at 2x and downsampled for smooth edges.
    /// Also used by the `--tray-preview` probe (which renders fixed sizes for
    /// the style/size matrix). Returns null on GDI failure.</summary>
    internal static uint[]? RenderPixels(TrayDisplayMode mode, TrayIconState? state, int size)
    {
        if (size is < 8 or > 256)
        {
            return null;
        }
        int src = size * 2; // 2x supersample of the target
        var hdc = GetDC(0);
        if (hdc == 0)
        {
            return null;
        }
        var mem = CreateCompatibleDC(hdc);
        if (mem == 0)
        {
            ReleaseDC(0, hdc);
            return null;
        }
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = src,
                biHeight = -src,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            },
        };
        var hbmp = CreateDIBSection(mem, ref bmi, DIB_RGB_COLORS, out var bits, 0, 0);
        if (hbmp == 0 || bits == 0)
        {
            DeleteDC(mem);
            ReleaseDC(0, hdc);
            return null;
        }
        var oldBmp = SelectObject(mem, hbmp);
        unsafe
        {
            new Span<byte>((void*)bits, src * src * 4).Clear();
        }

        uint[] output;
        if (mode == TrayDisplayMode.Number)
        {
            DrawDigits(mem, state, src);
            // Number mode always colorizes: charging / saver / disconnected
            // keep their shared state colors, plain discharge gets the
            // green→red level palette (a numeric icon is a colored number).
            var color = state is { } s
                ? BatteryColors.LevelFillColor(s.Level, s.Connected, s.Saver, s.Charging, colorize: true)
                : new RgbColor(0x80, 0x80, 0x80);
            unsafe
            {
                output = DownsampleDigits((uint*)bits, src, size, color);
            }
        }
        else
        {
            DrawGlyph(mem, state, src);
            unsafe
            {
                output = DownsampleGlyph((uint*)bits, src, size);
            }
        }

        // GDI refuses DeleteObject while an object is still selected into a
        // DC (the handle silently leaks): restore the bitmap before deleting.
        SelectObject(mem, oldBmp);
        DeleteObject(hbmp);
        DeleteDC(mem);
        ReleaseDC(0, hdc);
        return output;
    }

    /// <summary>`--tray-preview` self-check: the full HICON construction path
    /// (DIB → CreateIconIndirect), which the pixel sheet does not exercise.
    /// A successful build is owned by the caller (destroy it with DestroyIcon).</summary>
    internal static bool TryBuildIcon(TrayDisplayMode mode, TrayIconState? state, int size, out IntPtr icon)
    {
        icon = RenderPixels(mode, state, size) is { } pixels ? CreateIconFromPixels(pixels, size) : 0;
        return icon != 0;
    }

    /// <summary>16x16 logical battery outline + level fill + charging bolt,
    /// scaled to the supersample buffer (the historical 16px geometry maps
    /// 1:1 at size 16 and scales with the DPI metric otherwise).</summary>
    private static void DrawGlyph(IntPtr mem, TrayIconState? state, int src)
    {
        static int S(int v, int src) => v * src / 16;
        // A recorded-but-offline device cannot be charging: a stale charging
        // flag must not draw the bolt over the neutral fill.
        var (level, charging) = state is { } s ? (s.Level, s.Charging && s.Connected) : (0, false);
        // A recorded-but-offline device (keep_recorded_devices fallback) keeps
        // its level fill but in the neutral gray of the disconnected state, so
        // the tray still says "already recorded", not "live reading".
        var fr = state is { } d
            ? d.Connected ? BatteryColors.ColorFor(d.Level) : new RgbColor(0x80, 0x80, 0x80)
            : new RgbColor(0x80, 0x80, 0x80);

        var white = CreateSolidBrush(0x00FF_FFFF);
        var fill = CreateSolidBrush(ColorRef(fr.R, fr.G, fr.B));
        var pen = CreatePen(PS_SOLID, Math.Max(1, src / 16), 0x00FF_FFFF);
        var oldPen = SelectObject(mem, pen);
        SelectObject(mem, GetStockObject(NULL_BRUSH));
        // Outline: 1,3 - 12,12; cap at 13,6 - 15,10.
        RoundRect(mem, S(1, src), S(3, src), S(12, src), S(12, src), S(2, src), S(2, src));
        var oldBrush = SelectObject(mem, white);
        Rectangle(mem, S(13, src), S(6, src), S(15, src), S(10, src));
        if (state is not null)
        {
            int fw = 9 * level / 100;
            if (fw > 0)
            {
                var rc = new RECT
                {
                    Left = S(2, src),
                    Top = S(4, src),
                    Right = S(2 + fw, src),
                    Bottom = S(11, src),
                };
                FillRect(mem, ref rc, fill);
            }
            if (charging)
            {
                var bolt = CreateSolidBrush(0x00FF_FFFF);
                var oldBolt = SelectObject(mem, bolt);
                POINT[] pts =
                [
                    new(S(7, src), S(3, src)),
                    new(S(4, src), S(8, src)),
                    new(S(6, src), S(8, src)),
                    new(S(5, src), S(12, src)),
                    new(S(8, src), S(7, src)),
                    new(S(6, src), S(7, src)),
                ];
                Polygon(mem, pts);
                SelectObject(mem, oldBolt);
                DeleteObject(bolt);
            }
        }
        SelectObject(mem, oldPen);
        SelectObject(mem, oldBrush);
        DeleteObject(pen);
        DeleteObject(white);
        DeleteObject(fill);
    }

    /// <summary>Percentage digits (or "--") centered in the supersample
    /// buffer, drawn WHITE: the downsample turns text intensity into alpha and
    /// tints it with the state color, so antialiased edges become real alpha
    /// instead of a dark color fringe. The face/height auto-fits the box:
    /// measured with DT_CALCRECT, then shrunk one pixel at a time until "100"
    /// fits both dimensions — never truncated. Fitting uses the digit INK
    /// height (cap height), not the font line box: digits occupy ~0.7em, so
    /// fitting tmHeight threw away a third of the icon (2026-11-10: the tray
    /// digits read too small). The ink box is also what gets centered.</summary>
    private static void DrawDigits(IntPtr mem, TrayIconState? state, int src)
    {
        var chars = TrayDisplay.DisplayText(state?.Level).ToCharArray();
        if (chars.Length == 0)
        {
            return;
        }
        // 1 logical px (2 supersample px) of breathing room on every side.
        int margin = Math.Max(2, src / 16);
        int maxW = src - margin * 2;
        int maxH = src - margin * 2;
        if (maxW < 4 || maxH < 4)
        {
            return;
        }

        SetBkMode(mem, TRANSPARENT);
        SetTextColor(mem, 0x00FF_FFFF);

        // One measurement at 2/3 of the box is enough to estimate the fitted
        // height (text metrics are near-linear in the font height), keeping
        // the shrink loop to a couple of iterations instead of maxH.
        // The start height is deliberately allowed ABOVE the pixel box: a GDI
        // font height is the line-cell height (~1.3em), while the digits only
        // fill the cap height (~0.7em), so a font that fits the box vertically
        // still wastes a third of the tile. The fit checks in the loop (advance
        // width and ink height) are what actually decide.
        int startH = maxH * 2;
        if (Measure(chars, mem, src, maxH, Math.Max(4, maxH * 2 / 3)) is { Width: > 0, InkHeight: > 0 } probe)
        {
            int byWidth = probe.FontHeight * maxW / probe.Width;
            int byHeight = probe.FontHeight * maxH / probe.InkHeight;
            startH = Math.Clamp(Math.Min(byWidth, byHeight), 4, maxH * 3);
        }
        for (int fontH = startH; fontH >= 4; fontH--)
        {
            if (Measure(chars, mem, src, maxH, fontH) is not { } m)
            {
                return; // font creation failed: leave the icon empty
            }
            if (m.Width > maxW || m.InkHeight > maxH)
            {
                continue;
            }
            DrawInkCentered(chars, mem, src, fontH, m);
            return;
        }
        // Even 4px did not measure inside the box (only possible for sizes far
        // below SM_CXSMICON): draw the smallest attempt rather than nothing.
        if (Measure(chars, mem, src, maxH, 4) is { } smallest)
        {
            DrawInkCentered(chars, mem, src, 4, smallest);
        }
    }

    /// <summary>`FontHeight` is the requested GDI height; `Width` the
    /// DT_CALCRECT advance; `InkHeight` the digit ink height (cap height,
    /// tmAscent minus internal leading — digits are lining figures) and
    /// `Ascent` the baseline distance used to center that ink.</summary>
    private readonly record struct TextMetrics(int FontHeight, int Width, int InkHeight, int Ascent);

    /// <summary>Measure `chars` at `fontHeight`: DT_CALCRECT for the advance
    /// width and text metrics for the digit ink height. Null when GDI cannot
    /// create the font.</summary>
    private static TextMetrics? Measure(char[] chars, IntPtr mem, int src, int maxH, int fontHeight)
    {
        var font = CreateFontW(fontHeight, 0, 0, 0, FW_SEMIBOLD, 0, 0, 0, DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY,
            DEFAULT_PITCH | FF_DONTCARE, NumberFontFace);
        if (font == 0)
        {
            return null;
        }
        var old = SelectObject(mem, font);
        var rc = new RECT { Left = 0, Top = 0, Right = src * 4, Bottom = maxH };
        DrawTextW(mem, chars, chars.Length, ref rc, DT_CALCRECT | DT_SINGLELINE);
        int ink = maxH;
        int ascent = maxH;
        if (GetTextMetricsW(mem, out var tm))
        {
            // Digits are cap-height glyphs; the leading above the cap is
            // empty space for them, so only the cap can be centered.
            ink = Math.Max(1, tm.tmAscent - tm.tmInternalLeading);
            ascent = tm.tmAscent;
        }
        SelectObject(mem, old);
        DeleteObject(font);
        return new TextMetrics(fontHeight, rc.Right, ink, ascent);
    }

    /// <summary>Draw at `fontHeight` so the digit ink box — not the font line
    /// box — is centered in the tile.</summary>
    private static void DrawInkCentered(char[] chars, IntPtr mem, int src, int fontHeight, TextMetrics m)
    {
        var font = CreateFontW(fontHeight, 0, 0, 0, FW_SEMIBOLD, 0, 0, 0, DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY,
            DEFAULT_PITCH | FF_DONTCARE, NumberFontFace);
        if (font == 0)
        {
            return;
        }
        var old = SelectObject(mem, font);
        // DT_TOP puts the line-box top at rc.Top; the digit ink starts
        // (ascent - ink) below it. Solve that for a centered ink box.
        int top = (src - m.InkHeight) / 2 - (m.Ascent - m.InkHeight);
        int left = (src - m.Width) / 2;
        var rc = new RECT
        {
            Left = left,
            Top = top,
            Right = left + Math.Max(m.Width, 1),
            Bottom = top + Math.Max(m.InkHeight * 2, 8),
        };
        DrawTextW(mem, chars, chars.Length, ref rc, DT_LEFT | DT_SINGLELINE | DT_TOP | DT_NOCLIP);
        SelectObject(mem, old);
        DeleteObject(font);
    }

    /// <summary>2x2 → size downsample of the glyph path: alpha = drawn-pixel
    /// coverage, color = average of drawn colors (the legacy formula).</summary>
    private static unsafe uint[] DownsampleGlyph(uint* srcPtr, int src, int size)
    {
        var output = new uint[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                uint r = 0, g = 0, b = 0, n = 0;
                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        uint v = srcPtr[((y * 2 + dy) * src) + (x * 2 + dx)];
                        if ((v & 0x00FF_FFFF) != 0)
                        {
                            r += v & 0xFF;
                            g += (v >> 8) & 0xFF;
                            b += (v >> 16) & 0xFF;
                            n += 1;
                        }
                    }
                }
                if (n > 0)
                {
                    output[(y * size) + x] = ((n * 255 / 4) << 24) | ((b / n) << 16) | ((g / n) << 8) | (r / n);
                }
            }
        }
        return output;
    }

    /// <summary>2x2 → size downsample of the digit path: white text intensity
    /// is the coverage (alpha), the color is the state color — this keeps
    /// antialiased glyph edges as real alpha instead of averaging them into a
    /// darker tint.</summary>
    private static unsafe uint[] DownsampleDigits(uint* srcPtr, int src, int size, RgbColor color)
    {
        // Output pixels are copied into a 32bpp DIB verbatim: byte order in
        // memory is B,G,R,A, so the uint is 0xAARRGGBB.
        uint rgb = ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        var output = new uint[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                uint coverage = 0;
                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        coverage += srcPtr[((y * 2 + dy) * src) + (x * 2 + dx)] & 0xFF;
                    }
                }
                uint alpha = (coverage + 2) / 4;
                if (alpha > 0)
                {
                    output[(y * size) + x] = (alpha << 24) | rgb;
                }
            }
        }
        return output;
    }

    /// <summary>Wrap the straight-alpha pixels in an HICON (32bpp color bitmap
    /// + all-zero mask: with any nonzero alpha byte the system composites
    /// per-pixel alpha and ignores the mask).</summary>
    private static IntPtr CreateIconFromPixels(uint[] pixels, int size)
    {
        // The template DC must stay live until after the last
        // CreateCompatibleDC — releasing it early relied on GDI handle reuse
        // luck in the old code.
        var hdc = GetDC(0);
        if (hdc == 0)
        {
            return 0;
        }
        var mem = CreateCompatibleDC(hdc);
        if (mem == 0)
        {
            ReleaseDC(0, hdc);
            return 0;
        }
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -size,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            },
        };
        var bmp = CreateDIBSection(mem, ref bmi, DIB_RGB_COLORS, out var bits, 0, 0);
        if (bmp == 0 || bits == 0)
        {
            DeleteDC(mem);
            ReleaseDC(0, hdc);
            return 0;
        }
        unsafe
        {
            pixels.AsSpan().CopyTo(new Span<uint>((void*)bits, size * size));
        }
        // Monochrome rows are WORD-aligned; zeros = no transparency bits.
        int maskStride = ((size + 15) / 16) * 2;
        var mask = CreateBitmap(size, size, 1, 1, new byte[maskStride * size]);
        var ii = new ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = bmp };
        var icon = mask != 0 ? CreateIconIndirect(ref ii) : 0;
        DeleteObject(mask);
        DeleteObject(bmp);
        DeleteDC(mem);
        ReleaseDC(0, hdc);
        return icon;
    }
}
