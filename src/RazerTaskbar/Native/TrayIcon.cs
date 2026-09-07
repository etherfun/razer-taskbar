//! Port of src/tray.rs: notification-area fallback icon. The taskbar hook is
//! primary; the tray icon keeps the app usable when the hook is occluded.
//! Right-clicking opens the same menu as the widget, double-clicking opens
//! the battery history window — both via the WM_TRAY callback.

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

    private static IntPtr _hwnd;
    /// <summary>Last displayed (tooltip, battery state): refresh() runs every
    /// second and a NIM_MODIFY is only worth doing when something changed.</summary>
    private static (string Tip, (int Level, bool Charging)? State)? _last;

    /// <summary>Create the tray icon (idempotent). `hwnd` receives WM_TRAY.
    /// Also the re-add path after an explorer restart: NIM_ADD with the same
    /// hWnd/uID replaces the registration, never duplicates it.</summary>
    public static void EnsureCreated(IntPtr hwnd)
    {
        _hwnd = hwnd;
        // Fresh registration: reset the dedup so the trailing refresh()
        // really re-applies icon + tooltip on the (new) taskbar.
        _last = null;
        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = hwnd,
            uID = TrayUid,
            uFlags = NIF_MESSAGE | NIF_TIP | NIF_ICON,
            uCallbackMessage = WmTray,
            szTip = "Razer Taskbar",
            hIcon = BuildIcon(null),
        };
        Shell_NotifyIconW(NIM_ADD, ref nid);
        // Vista+: ask for the callback version we use.
        nid.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, ref nid);
        Refresh();
    }

    /// <summary>Rebuild icon + tooltip from the current device state (deduped).</summary>
    public static void Refresh()
    {
        if (_hwnd == 0)
        {
            return;
        }
        string tip;
        (int Level, bool Charging)? state;
        var device = DisplayModeResolver.Pick(
            AppState.Instance.Devices.Snapshot(), AppState.Instance.ConfigSnapshot(),
            AppState.Instance.ModeState, Environment.TickCount64);
        if (device is { } d)
        {
            var charging = d.IsCharging ? $" {I18n.Tr("(charging)")}" : "";
            tip = $"{d.Name}: {d.BatteryPercentage}%{charging}";
            // Predicted usage time / time-to-full from history.
            if (HistoryService.EstimateFor(d.Handle) is { } e)
            {
                tip += $" · {HistoryService.FormatEstimateVerbose(e)}";
            }
            state = (d.BatteryPercentage, d.IsCharging);
        }
        else
        {
            tip = I18n.Tr("No devices found");
            state = null;
        }
        if (_last == (tip, state))
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
            hIcon = BuildIcon(state),
        };
        Shell_NotifyIconW(NIM_MODIFY, ref nid);
        _last = (tip, state);
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
    }

    /// <summary>Settings-page toggle: create or remove the icon.</summary>
    public static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (_hwnd == 0 && WidgetThread.Hwnd != 0)
            {
                EnsureCreated(WidgetThread.Hwnd);
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

    /// <summary>16x16 tray icon (32bpp, per-pixel alpha): battery outline +
    /// level fill + charging bolt, drawn at 2x and downsampled for smooth
    /// edges. Alpha comes from drawn-pixel coverage.</summary>
    private static IntPtr BuildIcon((int Level, bool Charging)? state)
    {
        const int src = 32; // 2x supersample of the 16x16 target
        var hdc = GetDC(0);
        var mem = CreateCompatibleDC(hdc);
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
        var old = SelectObject(mem, hbmp);
        unsafe
        {
            new Span<byte>((void*)bits, src * src * 4).Clear();
        }

        var (level, charging) = state ?? (0, false);
        var fr = state is { } s ? BatteryColors.ColorFor(s.Level) : new RgbColor(0x80, 0x80, 0x80);

        static int S(int v) => v * 2;
        var white = CreateSolidBrush(0x00FF_FFFF);
        var fill = CreateSolidBrush(GdiText.ColorRef(fr.R, fr.G, fr.B));
        var pen = CreatePen(PS_SOLID, 2, 0x00FF_FFFF);
        SelectObject(mem, pen);
        SelectObject(mem, GetStockObject(NULL_BRUSH));
        // Outline: 1,3 - 12,12; cap at 13,6 - 14,9.
        RoundRect(mem, S(1), S(3), S(12), S(12), 4, 4);
        SelectObject(mem, white);
        Rectangle(mem, S(13), S(6), S(15), S(10));
        if (state is not null)
        {
            int fw = 9 * level / 100;
            if (fw > 0)
            {
                var rc = new RECT { Left = S(2), Top = S(4), Right = S(2 + fw), Bottom = S(11) };
                FillRect(mem, ref rc, fill);
            }
            if (charging)
            {
                var bolt = CreateSolidBrush(0x00FF_FFFF);
                SelectObject(mem, bolt);
                POINT[] pts =
                [
                    new(S(7), S(3)),
                    new(S(4), S(8)),
                    new(S(6), S(8)),
                    new(S(5), S(12)),
                    new(S(8), S(7)),
                    new(S(6), S(7)),
                ];
                Polygon(mem, pts);
                DeleteObject(bolt);
            }
        }
        SelectObject(mem, old);
        DeleteObject(pen);
        DeleteObject(white);
        DeleteObject(fill);

        // Downsample 2x2 -> 16x16 straight-alpha pixels: alpha = drawn-pixel
        // coverage, color = average of drawn colors.
        var output = new uint[256];
        unsafe
        {
            var srcPtr = (uint*)bits;
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    uint r = 0, g = 0, b = 0, n = 0;
                    for (int dy = 0; dy < 2; dy++)
                    {
                        for (int dx = 0; dx < 2; dx++)
                        {
                            uint v = srcPtr[((y * 2 + dy) * 32) + (x * 2 + dx)];
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
                        output[(y * 16) + x] = ((n * 255 / 4) << 24) | ((b / n) << 16) | ((g / n) << 8) | (r / n);
                    }
                }
            }
        }
        DeleteObject(hbmp);
        DeleteDC(mem);
        ReleaseDC(0, hdc);

        // 16x16 32bpp color bitmap with the computed alpha.
        var outBmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = 16,
                biHeight = -16,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            },
        };
        var mem2 = CreateCompatibleDC(hdc);
        var outBmp = CreateDIBSection(mem2, ref outBmi, DIB_RGB_COLORS, out var outBits, 0, 0);
        unsafe
        {
            output.AsSpan().CopyTo(new Span<uint>((void*)outBits, 256));
        }

        // Mask all-zero (= opaque): with any nonzero alpha byte the system
        // composites per-pixel alpha and ignores the mask.
        var maskBits = new byte[32];
        var mask = CreateBitmap(16, 16, 1, 1, maskBits);
        var ii = new ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = outBmp };
        var icon = CreateIconIndirect(ref ii);
        DeleteObject(mask);
        DeleteObject(outBmp);
        DeleteDC(mem2);
        return icon;
    }
}
