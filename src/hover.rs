//! Hover popover listing every known Razer device (name, battery, charging).
//!
//! The widget overlay is fully click-through (`WM_NCHITTEST` →
//! `HTTRANSPARENT`), so it never receives mouse input; hover is detected by
//! polling the cursor against the widget rect on a fast timer (`TIMER_HOVER`
//! in window.rs — GetCursorPos + PtInRect, ~free at 120ms). The panel is a
//! topmost layered `NOACTIVATE` window that never takes focus and is itself
//! click-through; it closes as soon as the cursor leaves the widget (or the
//! panel, so reading it never flickers). Same single-call colorkey rule and
//! static-state pattern as the widget window (`window.rs STATE`, `tray.rs`).

use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use windows::core::{w, PCWSTR};
use windows::Win32::Foundation::*;
use windows::Win32::Graphics::Gdi::*;
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::HiDpi::GetDpiForWindow;
use windows::Win32::UI::WindowsAndMessaging::*;

use crate::battery::{color_for, fluent_battery_glyph, pick_device_to_display, DeviceKind, DeviceMap};
use crate::taskbar::window_rect;

const CLASS_NAME: PCWSTR = w!("RazerTaskbarHover");
/// Rest the cursor on the widget this long before the panel appears.
const HOVER_DELAY: Duration = Duration::from_millis(350);

/// One panel row: pre-rendered strings + drawing state (snapshot — painting
/// never touches the device map).
#[derive(Debug, Clone, PartialEq)]
struct Row {
    name: String,
    pct: String,
    /// Predicted usage time / time-to-full (compact, e.g. "~3h25m"); empty
    /// when history has not enough data or the device is disconnected.
    eta: String,
    level: u8,
    charging: bool,
    connected: bool,
    displayed: bool,
    kind: DeviceKind,
}

// UI-thread-only state (same pattern as window.rs STATE / tray.rs statics).
static mut DEVICES: Option<Arc<Mutex<DeviceMap>>> = None;
static mut HOVER_SINCE: Option<Instant> = None;
static mut SHOWN: bool = false;
static mut ROWS: Vec<Row> = Vec::new();
static mut PANEL_POS: (i32, i32) = (0, 0);
static mut PANEL_SIZE: (i32, i32) = (0, 0);
static mut HOVER_WND: HWND = HWND(std::ptr::null_mut());

pub fn set_devices(devices: Arc<Mutex<DeviceMap>>) {
    #[allow(static_mut_refs)]
    unsafe {
        DEVICES = Some(devices);
    }
}

/// Fast timer tick: show/hide/update the panel from cursor proximity.
pub fn track(widget: HWND, enabled: bool) {
    unsafe {
        if !enabled {
            hide();
            return;
        }
        let Some(wrect) = window_rect(widget) else {
            hide();
            return;
        };
        let mut pt = POINT::default();
        let _ = GetCursorPos(&mut pt);
        let over_widget = PtInRect(&wrect, pt).as_bool();
        #[allow(static_mut_refs)]
        let over_panel = SHOWN && PtInRect(&panel_rect(), pt).as_bool();
        if !over_widget && !over_panel {
            hide();
            return;
        }
        #[allow(static_mut_refs)]
        let hovered_long = match HOVER_SINCE {
            Some(t) => t.elapsed() >= HOVER_DELAY,
            None => {
                HOVER_SINCE = Some(Instant::now());
                false
            }
        };
        #[allow(static_mut_refs)]
        if !SHOWN && !hovered_long {
            return;
        }
        update_and_show(widget, &wrect);
    }
}

/// Hide the panel (cursor left, or the feature was switched off).
pub fn hide() {
    unsafe {
        #[allow(static_mut_refs)]
        {
            HOVER_SINCE = None;
            if SHOWN {
                SHOWN = false;
                if !HOVER_WND.0.is_null() {
                    let _ = ShowWindow(HOVER_WND, SW_HIDE);
                }
            }
        }
    }
}

pub fn destroy() {
    unsafe {
        #[allow(static_mut_refs)]
        {
            HOVER_SINCE = None;
            SHOWN = false;
            ROWS.clear();
            if !HOVER_WND.0.is_null() {
                let _ = DestroyWindow(HOVER_WND);
                HOVER_WND = HWND(std::ptr::null_mut());
            }
        }
    }
}

fn panel_rect() -> RECT {
    #[allow(static_mut_refs)]
    unsafe {
        RECT {
            left: PANEL_POS.0,
            top: PANEL_POS.1,
            right: PANEL_POS.0 + PANEL_SIZE.0,
            bottom: PANEL_POS.1 + PANEL_SIZE.1,
        }
    }
}

fn no_devices_row() -> Row {
    Row {
        name: crate::i18n::tr("No Razer devices found").into(),
        pct: "--".into(),
        eta: String::new(),
        level: 0,
        charging: false,
        connected: false,
        displayed: false,
        kind: DeviceKind::Other,
    }
}

/// Immutable snapshot of the device list: connected first (then by name).
/// The device the widget currently shows gets the `displayed` highlight.
fn snapshot_rows() -> Vec<Row> {
    let devices_arc = unsafe {
        #[allow(static_mut_refs)]
        DEVICES.clone()
    };
    let Some(arc) = devices_arc else {
        return vec![no_devices_row()];
    };
    let devices = arc.lock().unwrap();
    if devices.is_empty() {
        return vec![no_devices_row()];
    }
    let shown = pick_device_to_display(&devices)
        .map(|d| d.handle)
        .unwrap_or_default();
    let mut rows: Vec<Row> = devices
        .values()
        .map(|d| Row {
            name: d.name.clone(),
            pct: format!("{}%", d.battery_percentage),
            eta: crate::history::estimate_for(&d.handle)
                .map(crate::history::format_estimate_compact)
                .unwrap_or_default(),
            level: d.battery_percentage,
            charging: d.is_charging,
            connected: d.is_connected,
            displayed: d.is_connected && d.handle == shown,
            kind: d.kind,
        })
        .collect();
    rows.sort_by(|a, b| {
        b.connected
            .cmp(&a.connected)
            .then_with(|| a.name.to_lowercase().cmp(&b.name.to_lowercase()))
    });
    rows
}

fn scale_of() -> f32 {
    unsafe {
        #[allow(static_mut_refs)]
        let dpi = if HOVER_WND.0.is_null() {
            96u32
        } else {
            GetDpiForWindow(HOVER_WND)
        };
        if dpi == 0 { 1.0 } else { dpi as f32 / 96.0 }
    }
}

fn create_icon_font(h: i32) -> HFONT {
    unsafe {
        let f = CreateFontW(
            h,
            0,
            0,
            0,
            FW_NORMAL.0 as i32,
            0,
            0,
            0,
            DEFAULT_CHARSET.0 as u32,
            OUT_DEFAULT_PRECIS.0 as u32,
            CLIP_DEFAULT_PRECIS.0 as u32,
            ANTIALIASED_QUALITY.0 as u32,
            (DEFAULT_PITCH.0 | FF_DONTCARE.0) as u32,
            w!("Segoe Fluent Icons"),
        );
        // Fallback for Win10 (no Fluent Icons font).
        if f.is_invalid() {
            CreateFontW(
                h, 0, 0, 0, FW_NORMAL.0 as i32, 0, 0, 0, DEFAULT_CHARSET.0 as u32,
                OUT_DEFAULT_PRECIS.0 as u32, CLIP_DEFAULT_PRECIS.0 as u32,
                ANTIALIASED_QUALITY.0 as u32, (DEFAULT_PITCH.0 | FF_DONTCARE.0) as u32,
                w!("Segoe MDL2 Assets"),
            )
        } else {
            f
        }
    }
}

fn create_text_font(h: i32) -> HFONT {
    unsafe {
        let f = CreateFontW(
            h,
            0,
            0,
            0,
            FW_SEMIBOLD.0 as i32,
            0,
            0,
            0,
            DEFAULT_CHARSET.0 as u32,
            OUT_DEFAULT_PRECIS.0 as u32,
            CLIP_DEFAULT_PRECIS.0 as u32,
            ANTIALIASED_QUALITY.0 as u32,
            (DEFAULT_PITCH.0 | FF_DONTCARE.0) as u32,
            w!("Segoe UI Variable Text"),
        );
        // Fallback if the Variable font is missing (Win10 / older Win11).
        if f.is_invalid() {
            CreateFontW(
                h, 0, 0, 0, FW_SEMIBOLD.0 as i32, 0, 0, 0, DEFAULT_CHARSET.0 as u32,
                OUT_DEFAULT_PRECIS.0 as u32, CLIP_DEFAULT_PRECIS.0 as u32,
                ANTIALIASED_QUALITY.0 as u32, (DEFAULT_PITCH.0 | FF_DONTCARE.0) as u32,
                w!("Segoe UI"),
            )
        } else {
            f
        }
    }
}

fn text_width(hdc: HDC, s: &str) -> i32 {
    if s.is_empty() {
        // DrawTextW with a zero-char slice still hands USER32 the Vec's
        // dangling sentinel pointer, which its prefix/shaping path reads →
        // access violation (crash was in TextShaping under DrawTextW).
        return 0;
    }
    unsafe {
        let mut wide: Vec<u16> = s.encode_utf16().collect();
        let mut rc = RECT::default();
        let _ = DrawTextW(hdc, &mut wide, &mut rc, DT_SINGLELINE | DT_CALCRECT | DT_LEFT);
        rc.right - rc.left
    }
}

/// Panel size for `rows` (measured with the real fonts, DPI-scaled).
fn measure(rows: &[Row]) -> (i32, i32) {
    let scale = scale_of();
    unsafe {
        let hdc = GetDC(None);
        let text_h = (14.0 * scale).round() as i32;
        let icon_h = (15.0 * scale).round() as i32;
        let text_font = create_text_font(text_h);
        let icon_font = create_icon_font(icon_h);
        let old = SelectObject(hdc, text_font);
        let mut name_w = 0;
        let mut pct_w = 0;
        let mut eta_w = 0;
        for r in rows {
            name_w = name_w.max(text_width(hdc, &r.name));
            pct_w = pct_w.max(text_width(hdc, &r.pct));
            eta_w = eta_w.max(text_width(hdc, &r.eta));
        }
        let any_eta = rows.iter().any(|r| !r.eta.is_empty());
        let eta_w = if any_eta { eta_w } else { 0 };
        let _ = SelectObject(hdc, icon_font);
        let glyph_w = text_width(hdc, "\u{E85A}"); // widest battery glyph
        let bolt_w = if rows.iter().any(|r| r.charging && r.level < 50) {
            text_width(hdc, "\u{EA93}")
        } else {
            0
        };
        let _ = SelectObject(hdc, old);
        let _ = DeleteObject(text_font);
        let _ = DeleteObject(icon_font);
        let _ = ReleaseDC(None, hdc);
        // Device-type icon column: widest kind at this row height.
        let kind_w = rows
            .iter()
            .map(|r| crate::icons::width_for(icon_h, r.kind))
            .max()
            .unwrap_or(0);
        let pad = (10.0 * scale).round() as i32;
        let gap = (6.0 * scale).round() as i32;
        let row_h = icon_h.max(text_h);
        let row_gap = (3.0 * scale).round() as i32;
        let n = rows.len() as i32;
        // Columns: [type icon] [glyph] [name] [bolt?] [eta?] [pct] — bolt and
        // eta only exist when some row needs them.
        let mut cols = kind_w + gap + glyph_w + gap + name_w + gap;
        if bolt_w > 0 {
            cols += bolt_w + gap;
        }
        if eta_w > 0 {
            cols += eta_w + gap;
        }
        cols += pct_w;
        let w = (pad * 2 + cols).max((150.0 * scale).round() as i32);
        let h = pad * 2 + n * row_h + (n - 1).max(0) * row_gap;
        (w, h)
    }
}

/// Place the panel against the widget: open upward from a bottom-docked
/// taskbar, downward from a top-docked one; x clamped into the work area.
fn place(widget: HWND, wrect: &RECT, w: i32, h: i32) -> (i32, i32) {
    unsafe {
        let mut mi = MONITORINFO {
            cbSize: std::mem::size_of::<MONITORINFO>() as u32,
            ..Default::default()
        };
        let hmon = MonitorFromWindow(widget, MONITOR_DEFAULTTONEAREST);
        if hmon.is_invalid() || !GetMonitorInfoW(hmon, &mut mi).as_bool() {
            return (wrect.left, wrect.top - h - 4);
        }
        let work = mi.rcWork;
        let x = wrect
            .left
            .clamp(work.left + 4, (work.right - w - 4).max(work.left + 4));
        let y = if wrect.top >= (work.top + work.bottom) / 2 {
            wrect.top - h - 4
        } else {
            wrect.bottom + 4
        };
        (x, y.max(work.top + 4))
    }
}

fn ensure_window() {
    unsafe {
        #[allow(static_mut_refs)]
        if !HOVER_WND.0.is_null() {
            return;
        }
        let instance = GetModuleHandleW(None).unwrap_or_default();
        let wc = WNDCLASSW {
            style: CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc: Some(hover_wnd_proc),
            hInstance: instance.into(),
            lpszClassName: CLASS_NAME,
            hCursor: LoadCursorW(None, IDC_ARROW).unwrap_or_default(),
            hbrBackground: HBRUSH(std::ptr::null_mut()),
            ..Default::default()
        };
        let _ = RegisterClassW(&wc); // re-registering fails harmlessly
        let hwnd = CreateWindowExW(
            WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE,
            CLASS_NAME,
            CLASS_NAME,
            WS_POPUP,
            0,
            0,
            10,
            10,
            None,
            None,
            instance,
            None,
        )
        .unwrap_or(HWND(std::ptr::null_mut()));
        if hwnd.0.is_null() {
            eprintln!(
                "razer-taskbar: hover panel CreateWindowExW failed: {:?}",
                GetLastError()
            );
            return;
        }
        // Same single-call colorkey rule as the widget (a second
        // SetLayeredWindowAttributes call would replace the key mode).
        let _ = SetLayeredWindowAttributes(hwnd, COLORREF(0x00000000), 0, LWA_COLORKEY);
        #[allow(static_mut_refs)]
        {
            HOVER_WND = hwnd;
        }
    }
}

fn update_and_show(widget: HWND, wrect: &RECT) {
    unsafe {
        ensure_window();
        #[allow(static_mut_refs)]
        if HOVER_WND.0.is_null() {
            return;
        }
        let rows = snapshot_rows();
        let (w, h) = measure(&rows);
        let pos = place(widget, wrect, w, h);
        #[allow(static_mut_refs)]
        let (rows_changed, geometry_changed) = {
            let rows_changed = ROWS != rows;
            let geometry_changed = PANEL_POS != pos || PANEL_SIZE != (w, h) || !SHOWN;
            ROWS = rows;
            PANEL_POS = pos;
            PANEL_SIZE = (w, h);
            SHOWN = true;
            (rows_changed, geometry_changed)
        };
        if geometry_changed {
            let _ = SetWindowPos(
                HOVER_WND,
                HWND_TOPMOST,
                pos.0,
                pos.1,
                w,
                h,
                SWP_NOACTIVATE | SWP_SHOWWINDOW,
            );
            let _ = InvalidateRect(HOVER_WND, None, true);
        } else if rows_changed {
            let _ = InvalidateRect(HOVER_WND, None, true);
        }
    }
}

unsafe extern "system" fn hover_wnd_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    match msg {
        WM_PAINT => {
            paint();
            LRESULT(0)
        }
        // Click-through like the widget: the panel is read-only info and
        // must never eat a click aimed at what is under it.
        WM_NCHITTEST => LRESULT(HTTRANSPARENT as isize),
        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}

/// Columns: [battery glyph] [name ...] [bolt] [pct]; bolt column exists only
/// when some row needs it (charging below 50% — above that the glyph itself
/// is the bolt variant). Opaque dark panel; the black colorkey only cuts the
/// outer margin, so the border/fill are solid.
fn paint() {
    unsafe {
        #[allow(static_mut_refs)]
        let hwnd = HOVER_WND;
        if hwnd.0.is_null() {
            return;
        }
        let mut ps = PAINTSTRUCT::default();
        let hdc = BeginPaint(hwnd, &mut ps);
        if hdc.is_invalid() {
            return;
        }
        #[allow(static_mut_refs)]
        let rows = ROWS.clone();
        #[allow(static_mut_refs)]
        let (w, h) = PANEL_SIZE;
        // Rounded dark panel: one RoundRect fills the body and strokes the
        // border; the square corners stay color-key black → transparent.
        let panel = RECT { left: 0, top: 0, right: w, bottom: h };
        let scale = scale_of();
        let radius = (8.0 * scale).round() as i32;
        let bg = CreateSolidBrush(COLORREF(0x00202020));
        let border = CreatePen(PS_SOLID, 1, COLORREF(0x005A5A5A));
        let old_pen = SelectObject(hdc, border);
        let old_brush = SelectObject(hdc, bg);
        let _ = RoundRect(hdc, panel.left, panel.top, panel.right, panel.bottom, radius * 2, radius * 2);
        let _ = SelectObject(hdc, old_pen);
        let _ = SelectObject(hdc, old_brush);
        let _ = DeleteObject(bg);
        let _ = DeleteObject(border);
        let _ = SetBkMode(hdc, TRANSPARENT);

        let pad = (10.0 * scale).round() as i32;
        let gap = (6.0 * scale).round() as i32;
        let text_h = (14.0 * scale).round() as i32;
        let icon_h = (15.0 * scale).round() as i32;
        let row_h = icon_h.max(text_h);
        let row_gap = (3.0 * scale).round() as i32;
        let icon_font = create_icon_font(icon_h);
        let text_font = create_text_font(text_h);
        let old = SelectObject(hdc, text_font);

        let mut pct_w = 0;
        let mut eta_w = 0;
        for r in &rows {
            pct_w = pct_w.max(text_width(hdc, &r.pct));
            eta_w = eta_w.max(text_width(hdc, &r.eta));
        }
        let any_eta = rows.iter().any(|r| !r.eta.is_empty());
        let eta_w = if any_eta { eta_w } else { 0 };
        let any_charging = rows.iter().any(|r| r.charging && r.level < 50);
        let _ = SelectObject(hdc, icon_font);
        let glyph_w = text_width(hdc, "\u{E85A}");
        let bolt_w = if any_charging { text_width(hdc, "\u{EA93}") } else { 0 };
        let _ = SelectObject(hdc, old);

        // Columns: [type icon] [battery glyph] [name ...] [bolt?] [eta?] [pct].
        let kind_w = rows
            .iter()
            .map(|r| crate::icons::width_for(icon_h, r.kind))
            .max()
            .unwrap_or(0);
        let pct_right = w - pad;
        let pct_left = pct_right - pct_w;
        // Columns grow leftward from the percentage: predicted time first,
        // then the standalone bolt.
        let mut left = pct_left - gap;
        let (eta_left, eta_right) = if any_eta {
            let r = left;
            let l = r - eta_w;
            left = l - gap;
            (l, r)
        } else {
            (0, 0)
        };
        let bolt_x = if bolt_w > 0 {
            let x = left - bolt_w;
            left = x - gap;
            x
        } else {
            0
        };
        let glyph_x = pad + kind_w + gap;
        let name_left = glyph_x + glyph_w + gap;
        let name_right = left.max(name_left);

        let mut y = pad;
        for r in &rows {
            let top = y;
            let bottom = y + row_h;
            let (cr, cg, cb) = if r.connected { color_for(r.level) } else { (0x80, 0x80, 0x80) };
            let level_color =
                COLORREF(cr as u32 | ((cg as u32) << 8) | ((cb as u32) << 16));
            // Device-type icon: light gray for connected, dim for not.
            // Centered in the kind column — kinds have different widths
            // (keyboard > mouse), and left-aligning made the column look
            // ragged. Also centered vertically in the row for safety.
            let kind_rgb = if r.connected { (0xE8, 0xE8, 0xE8) } else { (0x78, 0x78, 0x78) };
            let iw = crate::icons::width_for(icon_h, r.kind);
            let ibh = row_h.min(icon_h);
            crate::icons::draw(
                hdc,
                pad + (kind_w - iw) / 2,
                top + (row_h - ibh) / 2,
                ibh,
                r.kind,
                kind_rgb,
            );
            // Battery glyph (level variant; bolt variant when charging >= 50%).
            let mut glyph: Vec<u16> = vec![fluent_battery_glyph(r.level, r.charging) as u16];
            let mut grc = RECT { left: glyph_x, top, right: glyph_x + glyph_w + 4, bottom };
            let _ = SelectObject(hdc, icon_font);
            let _ = SetTextColor(hdc, level_color);
            DrawTextW(hdc, &mut glyph, &mut grc, DT_SINGLELINE | DT_VCENTER | DT_LEFT);
            // Name: the device the widget shows is bright white, other
            // connected ones slightly dimmer, disconnected ones gray.
            let name_color = if r.displayed {
                COLORREF(0x00FFFFFF)
            } else if r.connected {
                COLORREF(0x00D8D8D8)
            } else {
                COLORREF(0x00909090)
            };
            let mut name: Vec<u16> = r.name.encode_utf16().collect();
            let mut nrc = RECT {
                left: name_left,
                top,
                right: name_right.max(name_left),
                bottom,
            };
            let _ = SelectObject(hdc, text_font);
            let _ = SetTextColor(hdc, name_color);
            if !r.name.is_empty() {
                DrawTextW(
                    hdc,
                    &mut name,
                    &mut nrc,
                    DT_SINGLELINE | DT_VCENTER | DT_LEFT | DT_END_ELLIPSIS,
                );
            }
            // Standalone charging bolt for charging rows below 50%.
            if any_charging && r.charging && r.level < 50 {
                let mut bolt: Vec<u16> = "\u{EA93}".encode_utf16().collect();
                let mut brc = RECT {
                    left: bolt_x,
                    top,
                    right: bolt_x + bolt_w + 4,
                    bottom,
                };
                let _ = SelectObject(hdc, icon_font);
                let _ = SetTextColor(hdc, COLORREF(0x00FFFFFF));
                DrawTextW(hdc, &mut bolt, &mut brc, DT_SINGLELINE | DT_VCENTER | DT_LEFT);
            }
            // Predicted usage time / time-to-full (right-aligned, dim).
            if any_eta && !r.eta.is_empty() {
                let mut eta: Vec<u16> = r.eta.encode_utf16().collect();
                let mut erc = RECT { left: eta_left - 2, top, right: eta_right, bottom };
                let _ = SelectObject(hdc, text_font);
                let _ = SetTextColor(hdc, COLORREF(0x00B0B0B0));
                DrawTextW(hdc, &mut eta, &mut erc, DT_SINGLELINE | DT_VCENTER | DT_RIGHT);
            }
            // Percentage, level-colored, right-aligned.
            let mut pct: Vec<u16> = r.pct.encode_utf16().collect();
            let mut prc = RECT { left: pct_left - 2, top, right: pct_right, bottom };
            let _ = SelectObject(hdc, text_font);
            let _ = SetTextColor(hdc, level_color);
            DrawTextW(hdc, &mut pct, &mut prc, DT_SINGLELINE | DT_VCENTER | DT_RIGHT);
            y += row_h + row_gap;
        }
        let _ = SelectObject(hdc, old);
        let _ = DeleteObject(icon_font);
        let _ = DeleteObject(text_font);
        let _ = EndPaint(hwnd, &ps);
    }
}
