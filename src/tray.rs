//! Notification-area (tray) fallback icon.
//!
//! The taskbar hook is primary; the tray icon exists so the app stays usable
//! when the hook is occluded (e.g. Win11 XAML overlay paints over the child).
//! Right-clicking the tray icon opens the same menu as the widget
//! (`window.rs:show_menu`), via a forwarded `WM_RBUTTONUP`.

use windows::Win32::Foundation::*;
use windows::Win32::UI::Shell::*;
use windows::Win32::UI::WindowsAndMessaging::*;

use crate::battery::{color_for, pick_device_to_display};
use crate::watcher::DeviceMap;
use std::sync::{Arc, Mutex};

const TRAY_UID: u32 = 1;
const WM_TRAY: u32 = WM_APP + 1;

static mut TRAY_HWND: HWND = HWND(std::ptr::null_mut());
static mut TRAY_DEVICES: Option<Arc<Mutex<DeviceMap>>> = None;
/// Last displayed (tooltip, battery state): refresh() runs every second,
/// and a NIM_MODIFY is only worth doing when something visible changed.
static mut TRAY_LAST: Option<(String, Option<(u8, bool)>)> = None;

/// Create the tray icon (idempotent). `hwnd` receives `WM_TRAY` callbacks.
/// Also the re-add path after an explorer restart: `NIM_ADD` with the same
/// hWnd/uID replaces the registration, never duplicates it.
pub fn ensure_created(hwnd: HWND) {
    unsafe {
        TRAY_HWND = hwnd;
        // Fresh registration: reset the dedup so the trailing refresh()
        // really re-applies icon + tooltip on the (new) taskbar.
        #[allow(static_mut_refs)]
        {
            TRAY_LAST = None;
        }
        let mut nid = NOTIFYICONDATAW {
            cbSize: std::mem::size_of::<NOTIFYICONDATAW>() as u32,
            hWnd: hwnd,
            uID: TRAY_UID,
            uFlags: NIF_MESSAGE | NIF_TIP | NIF_ICON,
            uCallbackMessage: WM_TRAY,
            ..Default::default()
        };
        nid.szTip = utf16_tip("Razer Taskbar");
        nid.hIcon = build_icon();
        let _ = Shell_NotifyIconW(NIM_ADD, &nid);
        // Vista+: ask for the callback version we use.
        nid.Anonymous.uVersion = NOTIFYICON_VERSION_4;
        let _ = Shell_NotifyIconW(NIM_SETVERSION, &nid);
        refresh();
    }
}

pub fn set_devices(devices: Arc<Mutex<DeviceMap>>) {
    unsafe {
        TRAY_DEVICES = Some(devices);
    }
}

/// Rebuild icon + tooltip from the current device state (deduped: a no-op
/// while tooltip and battery state are unchanged).
pub fn refresh() {
    unsafe {
        if TRAY_HWND.0.is_null() {
            return;
        }
        #[allow(static_mut_refs)]
        let tray_devices: Option<&Arc<Mutex<DeviceMap>>> = TRAY_DEVICES.as_ref();
        let (tip, state) = match tray_devices {
            Some(devices) => {
                let devices = devices.lock().unwrap();
                match pick_device_to_display(&devices) {
                    Some(d) => {
                        let mut tip = format!(
                            "{}: {}%{}",
                            d.name,
                            d.battery_percentage,
                            if d.is_charging { " (charging)" } else { "" }
                        );
                        // Predicted usage time / time-to-full from history.rs.
                        if let Some(e) = crate::history::estimate_for(&d.handle) {
                            tip.push_str(&format!(" · {}", crate::history::format_estimate_verbose(e)));
                        }
                        (tip, Some((d.battery_percentage, d.is_charging)))
                    }
                    None => ("No devices found.".into(), None),
                }
            }
            None => ("Razer Taskbar".into(), None),
        };
        #[allow(static_mut_refs)]
        if TRAY_LAST.as_ref() == Some(&(tip.clone(), state)) {
            return;
        }
        let icon = build_icon_for(state);
        let mut nid = NOTIFYICONDATAW {
            cbSize: std::mem::size_of::<NOTIFYICONDATAW>() as u32,
            hWnd: TRAY_HWND,
            uID: TRAY_UID,
            uFlags: NIF_TIP | NIF_ICON,
            ..Default::default()
        };
        nid.szTip = utf16_tip(&tip);
        nid.hIcon = icon;
        let _ = Shell_NotifyIconW(NIM_MODIFY, &nid);
        #[allow(static_mut_refs)]
        {
            TRAY_LAST = Some((tip, state));
        }
    }
}

pub fn destroy() {
    unsafe {
        if TRAY_HWND.0.is_null() {
            return;
        }
        let nid = NOTIFYICONDATAW {
            cbSize: std::mem::size_of::<NOTIFYICONDATAW>() as u32,
            hWnd: TRAY_HWND,
            uID: TRAY_UID,
            ..Default::default()
        };
        let _ = Shell_NotifyIconW(NIM_DELETE, &nid);
        TRAY_HWND = HWND(std::ptr::null_mut());
        #[allow(static_mut_refs)]
        {
            TRAY_LAST = None;
        }
    }
}

pub fn tray_callback_msg() -> u32 {
    WM_TRAY
}

fn utf16_tip(s: &str) -> [u16; 128] {
    let mut buf = [0u16; 128];
    for (i, c) in s.encode_utf16().take(127).enumerate() {
        buf[i] = c;
    }
    buf
}

fn build_icon() -> HICON {
    build_icon_for(None)
}

/// 16x16 tray icon (32bpp, per-pixel alpha): battery outline + level fill +
/// charging bolt, drawn at 2x and downsampled for smooth edges. The DIB is
/// zero-initialized and alpha is computed from coverage — an uninitialized
/// bitmap used to composite as an opaque black square behind the glyph.
fn build_icon_for(state: Option<(u8, bool)>) -> HICON {
    use windows::Win32::Graphics::Gdi::*;
    const SRC: i32 = 32; // 2x supersample of the 16x16 target
    unsafe {
        let hdc = GetDC(None);
        let mem = CreateCompatibleDC(hdc);
        let bmi = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
                biWidth: SRC,
                biHeight: -SRC,
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB.0,
                ..Default::default()
            },
            ..Default::default()
        };
        let mut bits: *mut std::ffi::c_void = std::ptr::null_mut();
        let hbmp = CreateDIBSection(mem, &bmi, DIB_RGB_COLORS, &mut bits, None, 0)
            .unwrap_or_default();
        let old = SelectObject(mem, hbmp);
        std::ptr::write_bytes(bits as *mut u8, 0, (SRC * SRC * 4) as usize);

        let (level, charging) = state.unwrap_or((0, false));
        let (fr, fg, fb) = match state {
            Some((l, _)) => color_for(l),
            None => (0x80, 0x80, 0x80),
        };
        let s = |v: i32| v * 2;
        let white = CreateSolidBrush(COLORREF(0x00FFFFFF));
        let fill = CreateSolidBrush(COLORREF(fr as u32 | ((fg as u32) << 8) | ((fb as u32) << 16)));
        let pen = CreatePen(PS_SOLID, 2, COLORREF(0x00FFFFFF));
        let _ = SelectObject(mem, pen);
        let _ = SelectObject(mem, GetStockObject(NULL_BRUSH));
        // Outline: 1,3 - 12,12; cap at 13,6 - 14,9.
        let _ = RoundRect(mem, s(1), s(3), s(12), s(12), 4, 4);
        let _ = SelectObject(mem, white);
        let _ = Rectangle(mem, s(13), s(6), s(15), s(10));
        if state.is_some() {
            let fw = 9 * level as i32 / 100;
            if fw > 0 {
                let rc = RECT { left: s(2), top: s(4), right: s(2 + fw), bottom: s(11) };
                let _ = FillRect(mem, &rc, fill);
            }
            if charging {
                let bolt = CreateSolidBrush(COLORREF(0x00FFFFFF));
                let _ = SelectObject(mem, bolt);
                let pts = [
                    POINT { x: s(7), y: s(3) },
                    POINT { x: s(4), y: s(8) },
                    POINT { x: s(6), y: s(8) },
                    POINT { x: s(5), y: s(12) },
                    POINT { x: s(8), y: s(7) },
                    POINT { x: s(6), y: s(7) },
                ];
                let _ = Polygon(mem, &pts);
                let _ = DeleteObject(bolt);
            }
        }
        let _ = SelectObject(mem, old);
        let _ = DeleteObject(pen);
        let _ = DeleteObject(white);
        let _ = DeleteObject(fill);

        // Downsample 2x2 -> 16x16 straight-alpha pixels: alpha = drawn-pixel
        // coverage, color = average of drawn colors (background stays clear).
        let src = bits as *const u32;
        let mut out: [u32; 256] = [0; 256];
        for y in 0..16usize {
            for x in 0..16usize {
                let (mut r, mut g, mut b, mut n) = (0u32, 0u32, 0u32, 0u32);
                for dy in 0..2usize {
                    for dx in 0..2usize {
                        let v = *src.add((y * 2 + dy) * 32 + (x * 2 + dx));
                        if v & 0x00FF_FFFF != 0 {
                            r += v & 0xFF;
                            g += (v >> 8) & 0xFF;
                            b += (v >> 16) & 0xFF;
                            n += 1;
                        }
                    }
                }
                if n > 0 {
                    out[y * 16 + x] = ((n * 255 / 4) << 24)
                        | ((b / n) << 16)
                        | ((g / n) << 8)
                        | (r / n);
                }
            }
        }
        let _ = DeleteObject(hbmp);
        let _ = DeleteDC(mem);
        ReleaseDC(None, hdc);

        // 16x16 32bpp color bitmap with the computed alpha.
        let mut out_bits: *mut std::ffi::c_void = std::ptr::null_mut();
        let out_bmi = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
                biWidth: 16,
                biHeight: -16,
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB.0,
                ..Default::default()
            },
            ..Default::default()
        };
        let out_bmp = CreateDIBSection(mem, &out_bmi, DIB_RGB_COLORS, &mut out_bits, None, 0)
            .unwrap_or_default();
        std::ptr::copy_nonoverlapping(
            out.as_ptr() as *const u8,
            out_bits as *mut u8,
            16 * 16 * 4,
        );

        // Mask all-zero (= opaque): with any nonzero alpha byte the system
        // composites per-pixel alpha and ignores the mask.
        let mask_bits = [0u8; 32];
        let mask = CreateBitmap(16, 16, 1, 1, Some(mask_bits.as_ptr() as *const std::ffi::c_void));
        let ii = ICONINFO { fIcon: true.into(), hbmMask: mask, hbmColor: out_bmp, ..Default::default() };
        let icon = CreateIconIndirect(&ii).unwrap_or_default();
        let _ = DeleteObject(mask);
        let _ = DeleteObject(out_bmp);
        icon
    }
}
