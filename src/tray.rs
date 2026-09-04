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

/// 16x16 tray icon drawn with GDI: battery outline + fill + bolt.
fn build_icon_for(state: Option<(u8, bool)>) -> HICON {
    use windows::Win32::Graphics::Gdi::*;
    unsafe {
        let hdc = GetDC(None);
        let mem = CreateCompatibleDC(hdc);
        let bmi = BITMAPINFO {
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
        let mut bits: *mut std::ffi::c_void = std::ptr::null_mut();
        let hbmp = CreateDIBSection(mem, &bmi, DIB_RGB_COLORS, &mut bits, None, 0)
            .unwrap_or_default();
        let old = SelectObject(mem, hbmp);
        // Transparent background.
        let _ = SetBkMode(mem, TRANSPARENT);

        let (level, charging) = state.unwrap_or((0, false));
        let (fr, fg, fb) = match state {
            Some((l, _)) => color_for(l),
            None => (0x80, 0x80, 0x80),
        };
        let white = CreateSolidBrush(COLORREF(0x00FFFFFF));
        let fill = CreateSolidBrush(COLORREF(fr as u32 | ((fg as u32) << 8) | ((fb as u32) << 16)));
        // Outline: 1,3 - 12,12; cap at 13,6 - 14,9.
        let pen = CreatePen(PS_SOLID, 1, COLORREF(0x00FFFFFF));
        let _ = SelectObject(mem, pen);
        let _ = SelectObject(mem, GetStockObject(NULL_BRUSH));
        let _ = RoundRect(mem, 1, 3, 12, 12, 2, 2);
        let _ = SelectObject(mem, white);
        let _ = Rectangle(mem, 13, 6, 15, 10);
        if state.is_some() {
            let fw = 9 * level as i32 / 100;
            if fw > 0 {
                let rc = RECT { left: 2, top: 4, right: 2 + fw, bottom: 11 };
                let _ = FillRect(mem, &rc, fill);
            }
            if charging {
                let bolt = CreateSolidBrush(COLORREF(0x00FFFFFF));
                let _ = SelectObject(mem, bolt);
                let pts = [
                    POINT { x: 7, y: 3 },
                    POINT { x: 4, y: 8 },
                    POINT { x: 6, y: 8 },
                    POINT { x: 5, y: 12 },
                    POINT { x: 8, y: 7 },
                    POINT { x: 6, y: 7 },
                ];
                let _ = Polygon(mem, &pts);
                let _ = DeleteObject(bolt);
            }
        }
        let _ = SelectObject(mem, old);
        let _ = DeleteObject(pen);
        let _ = DeleteObject(white);
        let _ = DeleteObject(fill);
        let _ = DeleteDC(mem);
        ReleaseDC(None, hdc);

        // All-white mask => fully opaque icon.
        let mask = CreateBitmap(16, 16, 1, 1, None);
        let ii = ICONINFO { fIcon: true.into(), hbmMask: mask, hbmColor: hbmp, ..Default::default() };
        let icon = CreateIconIndirect(&ii).unwrap_or_default();
        let _ = DeleteObject(mask);
        let _ = DeleteObject(hbmp);
        icon
    }
}
