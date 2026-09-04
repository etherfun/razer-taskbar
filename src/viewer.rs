//! "Battery history" viewer: dark Win11-style window (DWM rounded corners +
//! immersive dark title bar, WinUI palette, card layout, owner-drawn range
//! pills and cycle list) with a level-over-time chart, cycle stats and the
//! raw cycle/session list. Opened from the widget/tray menu
//! (`ID_HISTORY_VIEW`); single instance, lives on the UI thread — the same
//! message loop as the widget — so no extra thread or sync needed.
//!
//! Chart semantics mirror `history.rs`: charging stretches are green bands,
//! discharge line white; off/unknown stretches (auto-shutdown, sleep) are
//! dark bands with NO line — the excluded usage time is visible as a gap.

#![allow(static_mut_refs)]

use std::iter::once;

use windows::core::{w, PCWSTR};
use windows::Win32::Foundation::*;
use windows::Win32::Graphics::Dwm::{
    DwmSetWindowAttribute, DWMWA_USE_IMMERSIVE_DARK_MODE, DWMWA_WINDOW_CORNER_PREFERENCE,
};
use windows::Win32::Graphics::Gdi::*;
use windows::Win32::Storage::FileSystem::FileTimeToLocalFileTime;
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Time::FileTimeToSystemTime;
use windows::Win32::UI::Controls::{DRAWITEMSTRUCT, ODS_SELECTED, SetWindowTheme};
use windows::Win32::UI::HiDpi::GetDpiForWindow;
use windows::Win32::UI::WindowsAndMessaging::*;

use crate::history::{self, Sample};

const CLASS_NAME: PCWSTR = w!("RazerTaskbarHistory");
const IDC_DEVICE: i32 = 1;
const IDC_RANGE7: i32 = 2;
const IDC_RANGE30: i32 = 3;
const IDC_RANGE_ALL: i32 = 4;
const IDC_LIST: i32 = 5;

// WinUI dark palette (COLORREF is 0x00BBGGRR).
const C_BG: COLORREF = COLORREF(0x001F1F1F);
const C_CARD: COLORREF = COLORREF(0x002B2B2B);
const C_CARD_BORDER: COLORREF = COLORREF(0x00383838);
const C_TEXT: COLORREF = COLORREF(0x00F3F3F3);
const C_TEXT2: COLORREF = COLORREF(0x00ACACAC);
const C_TEXT3: COLORREF = COLORREF(0x007A7A7A);
const C_ACCENT: COLORREF = COLORREF(0x00FFCD60); // #60CDFF
const C_ACCENT_TEXT: COLORREF = COLORREF(0x005C3A00); // #003A5C on accent
const C_GREEN: COLORREF = COLORREF(0x005FCB6C); // #6CCB5F
const C_GREEN_BAND: COLORREF = COLORREF(0x002A4026); // charging band on card
const C_ORANGE: COLORREF = COLORREF(0x0000B9FF); // #FFB900 swap marker
const C_GRID: COLORREF = COLORREF(0x00333333);
const C_ROW_SEL: COLORREF = COLORREF(0x003A3A3A);
const C_LINE: COLORREF = COLORREF(0x00E8E8E8); // discharge line
const C_OFF_BAND: COLORREF = COLORREF(0x00252525); // off/unknown band
const C_AREA: COLORREF = COLORREF(0x00323232); // fill under the level line

#[derive(Clone)]
struct ListItem {
    charge: bool,
    start: String,
    dur: String,
    levels: String,
}

struct ViewerState {
    hwnd: HWND,
    combo: HWND,
    list: HWND,
    btn7: HWND,
    btn30: HWND,
    btn_all: HWND,
    devices: Vec<(String, String)>,
    handle: String,
    /// 7 / 30 / 0 = all.
    range_days: i64,
    samples: Vec<Sample>,
    items: Vec<ListItem>,
}

// UI-thread-only state (same pattern as window.rs STATE / hover.rs statics).
static mut VIEWER: Option<ViewerState> = None;
static mut CLASS_OK: bool = false;
static mut TITLE_FONT: HFONT = HFONT(std::ptr::null_mut());
static mut BODY_FONT: HFONT = HFONT(std::ptr::null_mut());
static mut CAPTION_FONT: HFONT = HFONT(std::ptr::null_mut());
static mut LIST_FONT: HFONT = HFONT(std::ptr::null_mut());
static mut CARD_BRUSH: HBRUSH = HBRUSH(std::ptr::null_mut());
static mut BG_BRUSH: HBRUSH = HBRUSH(std::ptr::null_mut());

/// Open (or focus) the history viewer window.
pub fn open() {
    unsafe {
        if let Some(v) = VIEWER.as_ref() {
            let _ = ShowWindow(v.hwnd, SW_SHOW);
            let _ = SetForegroundWindow(v.hwnd);
            return;
        }
        register_class();
        let instance = GetModuleHandleW(None).unwrap_or_default();
        let hwnd = CreateWindowExW(
            WINDOW_EX_STYLE(0),
            CLASS_NAME,
            w!("Battery history — Razer Taskbar"),
            WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            920,
            660,
            None,
            None,
            instance,
            None,
        )
        .unwrap_or(HWND(std::ptr::null_mut()));
        if hwnd.0.is_null() {
            eprintln!("razer-taskbar: history viewer window create failed");
            return;
        }
        // Win11 chrome: immersive dark title bar + rounded corners (best
        // effort — old builds reject the attributes and stay default).
        let dark: i32 = 1;
        let _ = DwmSetWindowAttribute(
            hwnd,
            DWMWA_USE_IMMERSIVE_DARK_MODE,
            &dark as *const i32 as *const core::ffi::c_void,
            4,
        );
        let round: i32 = 2; // DWMWCP_ROUND
        let _ = DwmSetWindowAttribute(
            hwnd,
            DWMWA_WINDOW_CORNER_PREFERENCE,
            &round as *const i32 as *const core::ffi::c_void,
            4,
        );

        let s = dpi_scale(hwnd);
        if TITLE_FONT.0.is_null() {
            TITLE_FONT = create_font((19.0 * s).round() as i32, 600, w!("Segoe UI Variable Display"));
            BODY_FONT = create_font((14.0 * s).round() as i32, 400, w!("Segoe UI Variable Text"));
            CAPTION_FONT = create_font((12.0 * s).round() as i32, 400, w!("Segoe UI Variable Text"));
            LIST_FONT = create_font((13.0 * s).round() as i32, 400, w!("Segoe UI Variable Text"));
            CARD_BRUSH = CreateSolidBrush(C_CARD);
            BG_BRUSH = CreateSolidBrush(C_BG);
        }

        let m = (12.0 * s).round() as i32;
        let pill_w = (76.0 * s).round() as i32;
        let pill_h = (34.0 * s).round() as i32;
        let combo = CreateWindowExW(
            WINDOW_EX_STYLE(0),
            w!("COMBOBOX"),
            PCWSTR::null(),
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | WINDOW_STYLE(CBS_DROPDOWNLIST as u32) | WS_VSCROLL,
            m,
            m,
            (260.0 * s).round() as i32,
            (240.0 * s).round() as i32,
            hwnd,
            HMENU(IDC_DEVICE as usize as *mut core::ffi::c_void),
            instance,
            None,
        )
        .unwrap_or_default();
        // Dark dropdown + dark list (Win10 1809+; best effort).
        let _ = SetWindowTheme(combo, w!("DarkMode_Explorer"), PCWSTR::null());
        let _ = SendMessageW(combo, WM_SETFONT, WPARAM(BODY_FONT.0 as usize), LPARAM(1));

        let mk_pill = |id: i32, text: PCWSTR| {
            CreateWindowExW(
                WINDOW_EX_STYLE(0),
                w!("BUTTON"),
                text,
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WINDOW_STYLE(BS_OWNERDRAW as u32),
                0,
                0,
                pill_w,
                pill_h,
                hwnd,
                HMENU(id as usize as *mut core::ffi::c_void),
                instance,
                None,
            )
            .unwrap_or_default()
        };
        let btn7 = mk_pill(IDC_RANGE7, w!("7 days"));
        let btn30 = mk_pill(IDC_RANGE30, w!("30 days"));
        let btn_all = mk_pill(IDC_RANGE_ALL, w!("All"));

        let devices = history::list_devices();
        let handle = devices.first().map(|(h, _)| h.clone()).unwrap_or_default();
        let mut st = ViewerState {
            hwnd,
            combo,
            list: HWND(std::ptr::null_mut()),
            btn7,
            btn30,
            btn_all,
            devices,
            handle,
            range_days: 30,
            samples: Vec::new(),
            items: Vec::new(),
        };
        // Device picker.
        for (_, name) in &st.devices {
            let wide: Vec<u16> = name.encode_utf16().chain(once(0)).collect();
            let _ = SendMessageW(combo, CB_ADDSTRING, WPARAM(0), LPARAM(wide.as_ptr() as isize));
        }
        if !st.devices.is_empty() {
            let _ = SendMessageW(combo, CB_SETCURSEL, WPARAM(0), LPARAM(0));
        }
        // Cycle list — owner-drawn rows on the card color.
        let list = CreateWindowExW(
            WINDOW_EX_STYLE(0),
            w!("LISTBOX"),
            PCWSTR::null(),
            WS_CHILD
                | WS_VISIBLE
                | WS_VSCROLL
                | WINDOW_STYLE((LBS_NOINTEGRALHEIGHT | LBS_OWNERDRAWFIXED | LBS_NOTIFY) as u32),
            0,
            0,
            10,
            10,
            hwnd,
            HMENU(IDC_LIST as usize as *mut core::ffi::c_void),
            instance,
            None,
        )
        .unwrap_or_default();
        let _ = SetWindowTheme(list, w!("DarkMode_Explorer"), PCWSTR::null());
        let _ = SendMessageW(list, LB_SETITEMHEIGHT, WPARAM(0), LPARAM((24.0 * s).round() as isize));
        for h in [btn7, btn30, btn_all] {
            let _ = SendMessageW(h, WM_SETFONT, WPARAM(BODY_FONT.0 as usize), LPARAM(1));
        }
        st.list = list;
        VIEWER = Some(st);
        layout(hwnd);
        reload();
    }
}

/// Close the viewer if open (main window WM_DESTROY).
pub fn destroy() {
    unsafe {
        if let Some(v) = VIEWER.take() {
            let _ = DestroyWindow(v.hwnd);
        }
    }
}

fn register_class() {
    unsafe {
        if CLASS_OK {
            return;
        }
        // BG brush on the class kills erase flicker; WM_PAINT redraws cards.
        let bg = CreateSolidBrush(C_BG);
        let wc = WNDCLASSW {
            style: CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc: Some(wnd_proc),
            hInstance: GetModuleHandleW(None).unwrap_or_default().into(),
            lpszClassName: CLASS_NAME,
            hCursor: LoadCursorW(None, IDC_ARROW).unwrap_or_default(),
            hbrBackground: bg,
            ..Default::default()
        };
        if RegisterClassW(&wc) != 0 {
            CLASS_OK = true;
        }
    }
}

unsafe extern "system" fn wnd_proc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    match msg {
        WM_COMMAND => {
            let id = (wparam.0 & 0xFFFF) as i32;
            let code = ((wparam.0 >> 16) & 0xFFFF) as i32;
            handle_command(id, code);
            LRESULT(0)
        }
        WM_DRAWITEM => {
            let dis = &mut *(lparam.0 as *mut DRAWITEMSTRUCT);
            draw_item(dis);
            LRESULT(1)
        }
        WM_CTLCOLORSTATIC | WM_CTLCOLORLISTBOX => {
            // Dark text on the card color for the combobox display and list.
            let hdc = HDC(wparam.0 as *mut core::ffi::c_void);
            let _ = SetTextColor(hdc, C_TEXT);
            let _ = SetBkMode(hdc, TRANSPARENT);
            LRESULT(CARD_BRUSH.0 as isize)
        }
        WM_GETMINMAXINFO => {
            let mmi = &mut *(lparam.0 as *mut MINMAXINFO);
            let s = dpi_scale(hwnd);
            mmi.ptMinTrackSize = POINT { x: (860.0 * s).round() as i32, y: (600.0 * s).round() as i32 };
            LRESULT(0)
        }
        WM_SIZE => {
            layout(hwnd);
            LRESULT(0)
        }
        WM_PAINT => {
            paint(hwnd);
            LRESULT(0)
        }
        WM_DESTROY => {
            VIEWER = None;
            LRESULT(0)
        }
        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}

fn handle_command(id: i32, code: i32) {
    unsafe {
        let Some(v) = VIEWER.as_ref() else { return };
        let clicked = code == BN_CLICKED as i32;
        match id {
            IDC_DEVICE if code == CBN_SELCHANGE as i32 => {
                let idx = SendMessageW(v.combo, CB_GETCURSEL, WPARAM(0), LPARAM(0)).0;
                let mut handle = String::new();
                if idx >= 0 {
                    if let Some((h, _)) = v.devices.get(idx as usize) {
                        handle = h.clone();
                    }
                }
                if let Some(v) = VIEWER.as_mut() {
                    v.handle = handle;
                }
                reload();
            }
            IDC_RANGE7 if clicked => set_range(7),
            IDC_RANGE30 if clicked => set_range(30),
            IDC_RANGE_ALL if clicked => set_range(0),
            _ => {}
        }
    }
}

fn set_range(days: i64) {
    unsafe {
        if let Some(v) = VIEWER.as_mut() {
            v.range_days = days;
        }
        reload();
    }
}

/// Re-query samples for the current device/range and refill the cycle list.
fn reload() {
    unsafe {
        let Some(v) = VIEWER.as_mut() else { return };
        let now = unix_now();
        let since = if v.range_days > 0 { now - v.range_days * 86400 } else { 0 };
        v.samples = history::samples_in_range(&v.handle, since);
        let (dis, chg) = history::compute_spans(&v.samples);
        let mut items: Vec<(i64, ListItem)> = Vec::new();
        for s in dis {
            items.push((
                s.end_ts,
                ListItem {
                    charge: false,
                    start: fmt_stamp(s.start_ts),
                    dur: history::format_duration(s.active_secs),
                    levels: format!("{}→{}%", s.level_start, s.level_end),
                },
            ));
        }
        for s in chg {
            items.push((
                s.end_ts,
                ListItem {
                    charge: true,
                    start: fmt_stamp(s.start_ts),
                    dur: history::format_duration(s.active_secs),
                    levels: format!("{}→{}%", s.level_start, s.level_end),
                },
            ));
        }
        items.sort_by(|a, b| b.0.cmp(&a.0));
        v.items = items.into_iter().map(|(_, it)| it).collect();
        let list = v.list;
        let hwnd = v.hwnd;
        let _ = SendMessageW(list, LB_RESETCONTENT, WPARAM(0), LPARAM(0));
        for _ in 0..v.items.len() {
            let _ = SendMessageW(list, LB_ADDSTRING, WPARAM(0), LPARAM(0));
        }
        let _ = InvalidateRect(hwnd, None, false);
    }
}

/// Toolbar + stats cards + chart card + list card geometry.
fn areas(rc: RECT, s: f32) -> (Vec<RECT>, RECT, RECT, RECT) {
    let m = (12.0 * s).round() as i32;
    let gap = (10.0 * s).round() as i32;
    let tool_h = (34.0 * s).round() as i32;
    let stats_h = (64.0 * s).round() as i32;

    let inner_w = rc.right - m * 2;
    let card_w = ((inner_w - gap * 3) / 4).max(10);
    let stats_y = m + tool_h + gap;
    let mut cards = Vec::new();
    for i in 0..4 {
        cards.push(RECT {
            left: m + i * (card_w + gap),
            top: stats_y,
            right: m + i * (card_w + gap) + card_w,
            bottom: stats_y + stats_h,
        });
    }
    let rest_top = stats_y + stats_h + gap;
    let rest_h = (rc.bottom - rest_top - m).max(60);
    let chart_h = rest_h * 52 / 100;
    let chart = RECT { left: m, top: rest_top, right: rc.right - m, bottom: rest_top + chart_h };
    let list = RECT {
        left: m,
        top: chart.bottom + gap,
        right: rc.right - m,
        bottom: rest_top + rest_h,
    };
    (cards, chart, list, RECT { left: m, top: m, right: rc.right - m, bottom: m + tool_h })
}

fn layout(hwnd: HWND) {
    unsafe {
        let Some(v) = VIEWER.as_ref() else { return };
        let mut rc = RECT::default();
        let _ = GetClientRect(hwnd, &mut rc);
        if rc.right <= 0 {
            return;
        }
        let s = dpi_scale(hwnd);
        let m = (12.0 * s).round() as i32;
        let pill_w = (76.0 * s).round() as i32;
        let pill_h = (34.0 * s).round() as i32;
        let gap = (8.0 * s).round() as i32;
        let _ = MoveWindow(v.combo, m, m, (260.0 * s).round() as i32, (240.0 * s).round() as i32, true);
        let mut x = rc.right - m - pill_w;
        let _ = MoveWindow(v.btn_all, x, m, pill_w, pill_h, true);
        x -= pill_w + gap;
        let _ = MoveWindow(v.btn30, x, m, pill_w, pill_h, true);
        x -= pill_w + gap;
        let _ = MoveWindow(v.btn7, x, m, pill_w, pill_h, true);
        let (_, _, list, _) = areas(rc, s);
        let pad = (10.0 * s).round() as i32;
        let _ = MoveWindow(
            v.list,
            list.left + pad,
            list.top + (26.0 * s).round() as i32,
            (list.right - list.left - pad * 2).max(10),
            (list.bottom - list.top - (26.0 * s).round() as i32 - pad).max(10),
            true,
        );
    }
}

fn paint(hwnd: HWND) {
    unsafe {
        let mut ps = PAINTSTRUCT::default();
        let hdc = BeginPaint(hwnd, &mut ps);
        if hdc.is_invalid() {
            return;
        }
        let mut rc = RECT::default();
        let _ = GetClientRect(hwnd, &mut rc);
        let bg = CreateSolidBrush(C_BG);
        let _ = FillRect(hdc, &rc, bg);
        let _ = DeleteObject(bg);
        let _ = SetBkMode(hdc, TRANSPARENT);
        let Some(v) = VIEWER.as_ref() else {
            let _ = EndPaint(hwnd, &ps);
            return;
        };
        let s = dpi_scale(hwnd);

        let (cards, chart, list, _) = areas(rc, s);

        // Stat cards: value + caption, WinUI style.
        let stats = history::cycle_stats(&v.samples);
        let per_charge = stats
            .use_hours_per_pct
            .map(|r| history::format_duration((r * 100.0 * 3600.0).round() as i64))
            .unwrap_or_else(|| "--".into());
        let charge_full = stats
            .charge_hours_per_pct
            .map(|r| history::format_duration((r * 100.0 * 3600.0).round() as i64))
            .unwrap_or_else(|| "--".into());
        let est = history::estimate_for(&v.handle);
        let now_val = est
            .map(|e| history::format_duration(e.secs))
            .unwrap_or_else(|| "--".into());
        let now_cap = match est {
            Some(e) if e.charging => "until full (now)",
            _ => "time remaining now",
        };
        let values = [
            (format!("{}", stats.cycles), "discharge cycles", C_TEXT),
            (per_charge, "per full charge", C_TEXT),
            (charge_full, "empty → full", C_TEXT),
            (now_val, now_cap, C_ACCENT),
        ];
        for (card, (value, caption, color)) in cards.iter().zip(values) {
            draw_card(hdc, *card, s);
            let pad = (12.0 * s).round() as i32;
            let mut vrc = RECT {
                left: card.left + pad,
                top: card.top + (8.0 * s).round() as i32,
                right: card.right - pad,
                bottom: card.top + (36.0 * s).round() as i32,
            };
            let mut val: Vec<u16> = value.encode_utf16().collect();
            if !val.is_empty() {
                let _ = SelectObject(hdc, TITLE_FONT);
                let _ = SetTextColor(hdc, color);
                DrawTextW(hdc, &mut val, &mut vrc, DT_SINGLELINE | DT_LEFT | DT_END_ELLIPSIS);
            }
            let mut crc = RECT {
                left: card.left + pad,
                top: card.top + (38.0 * s).round() as i32,
                right: card.right - pad,
                bottom: card.bottom - (6.0 * s).round() as i32,
            };
            let mut cap: Vec<u16> = caption.encode_utf16().collect();
            if !cap.is_empty() {
                let _ = SelectObject(hdc, CAPTION_FONT);
                let _ = SetTextColor(hdc, C_TEXT2);
                DrawTextW(hdc, &mut cap, &mut crc, DT_SINGLELINE | DT_LEFT | DT_END_ELLIPSIS);
            }
        }

        draw_chart(hdc, v, chart, s);

        // List card: caption row + the child listbox sits on top (layout()).
        draw_card(hdc, list, s);
        let pad = (12.0 * s).round() as i32;
        let mut cap: Vec<u16> = format!(
            "Cycles & charging sessions ({} recorded)",
            v.items.len()
        )
        .encode_utf16()
        .collect();
        let mut crc = RECT {
            left: list.left + pad,
            top: list.top + (7.0 * s).round() as i32,
            right: list.right - pad,
            bottom: list.top + (24.0 * s).round() as i32,
        };
        let _ = SelectObject(hdc, CAPTION_FONT);
        let _ = SetTextColor(hdc, C_TEXT2);
        DrawTextW(hdc, &mut cap, &mut crc, DT_SINGLELINE | DT_LEFT | DT_END_ELLIPSIS);

        let _ = EndPaint(hwnd, &ps);
    }
}

fn draw_card(hdc: HDC, rc: RECT, s: f32) {
    unsafe {
        let radius = (8.0 * s).round() as i32;
        let brush = CreateSolidBrush(C_CARD);
        let pen = CreatePen(PS_SOLID, 1, C_CARD_BORDER);
        let old_pen = SelectObject(hdc, pen);
        let old_brush = SelectObject(hdc, brush);
        let _ = RoundRect(hdc, rc.left, rc.top, rc.right, rc.bottom, radius * 2, radius * 2);
        let _ = SelectObject(hdc, old_pen);
        let _ = SelectObject(hdc, old_brush);
        let _ = DeleteObject(brush);
        let _ = DeleteObject(pen);
    }
}

/// Owner-drawn range pills (segmented accent style) and cycle-list rows.
unsafe fn draw_item(dis: &mut DRAWITEMSTRUCT) {
    let Some(v) = VIEWER.as_ref() else { return };
    let mut rc = dis.rcItem;
    let hdc = dis.hDC;
    let s = dpi_scale(dis.hwndItem);
    match dis.CtlID as i32 {
        IDC_RANGE7 | IDC_RANGE30 | IDC_RANGE_ALL => {
            let days = match dis.CtlID as i32 {
                IDC_RANGE7 => 7,
                IDC_RANGE30 => 30,
                _ => 0,
            };
            let selected = v.range_days == days;
            let (fill, text_color) = if selected {
                (C_ACCENT, C_ACCENT_TEXT)
            } else if dis.itemState.0 & ODS_SELECTED.0 != 0 {
                (C_ROW_SEL, C_TEXT)
            } else {
                (C_CARD, C_TEXT2)
            };
            let radius = (rc.bottom - rc.top) / 2;
            let brush = CreateSolidBrush(fill);
            let pen = CreatePen(PS_SOLID, 1, if selected { C_ACCENT } else { C_CARD_BORDER });
            let old_pen = SelectObject(hdc, pen);
            let old_brush = SelectObject(hdc, brush);
            let _ = RoundRect(hdc, rc.left, rc.top, rc.right, rc.bottom, radius * 2, radius * 2);
            let _ = SelectObject(hdc, old_pen);
            let _ = SelectObject(hdc, old_brush);
            let _ = DeleteObject(brush);
            let _ = DeleteObject(pen);
            let _ = SetBkMode(hdc, TRANSPARENT);
            let text: String = match dis.CtlID as i32 {
                IDC_RANGE7 => "7 days".into(),
                IDC_RANGE30 => "30 days".into(),
                _ => "All".into(),
            };
            let mut wide: Vec<u16> = text.encode_utf16().collect();
            if !wide.is_empty() {
                let _ = SelectObject(hdc, BODY_FONT);
                let _ = SetTextColor(hdc, text_color);
                DrawTextW(hdc, &mut wide, &mut rc, DT_SINGLELINE | DT_CENTER | DT_VCENTER);
            }
        }
        IDC_LIST => {
            let idx = dis.itemID as usize;
            let Some(item) = v.items.get(idx) else { return };
            let selected = dis.itemState.0 & ODS_SELECTED.0 != 0;
            if selected {
                let fill = CreateSolidBrush(C_ROW_SEL);
                let old = SelectObject(hdc, fill);
                let r = (6.0 * s).round() as i32;
                let _ = RoundRect(hdc, rc.left + 2, rc.top + 1, rc.right - 2, rc.bottom - 1, r * 2, r * 2);
                let _ = SelectObject(hdc, old);
                let _ = DeleteObject(fill);
            }
            let _ = SetBkMode(hdc, TRANSPARENT);
            let _ = SelectObject(hdc, LIST_FONT);
            let pad = (10.0 * s).round() as i32;
            let row_h = rc.bottom - rc.top;
            let cy = rc.top + row_h / 2;

            // Type badge: USE (outline) / CHARGE (green pill).
            let badge_w = (52.0 * s).round() as i32;
            let badge_h = (18.0 * s).round() as i32;
            let bx = rc.left + pad;
            let by = cy - badge_h / 2;
            let (badge_text, badge_fill, badge_border, badge_text_color) = if item.charge {
                ("CHARGE", C_GREEN_BAND, C_GREEN, C_GREEN)
            } else {
                ("USE", C_CARD, C_TEXT3, C_TEXT2)
            };
            let bbrush = CreateSolidBrush(badge_fill);
            let bpen = CreatePen(PS_SOLID, 1, badge_border);
            let ob = SelectObject(hdc, bpen);
            let of = SelectObject(hdc, bbrush);
            let _ = RoundRect(hdc, bx, by, bx + badge_w, by + badge_h, badge_h, badge_h);
            let _ = SelectObject(hdc, ob);
            let _ = SelectObject(hdc, of);
            let _ = DeleteObject(bbrush);
            let _ = DeleteObject(bpen);
            let mut btxt: Vec<u16> = badge_text.encode_utf16().collect();
            let mut brc = RECT { left: bx, top: by, right: bx + badge_w, bottom: by + badge_h };
            let _ = SetTextColor(hdc, badge_text_color);
            DrawTextW(hdc, &mut btxt, &mut brc, DT_SINGLELINE | DT_CENTER | DT_VCENTER);

            // Start stamp.
            let mut stamp: Vec<u16> = item.start.encode_utf16().collect();
            let mut src = RECT {
                left: bx + badge_w + pad,
                top: rc.top,
                right: bx + badge_w + pad + (170.0 * s).round() as i32,
                bottom: rc.bottom,
            };
            let _ = SetTextColor(hdc, C_TEXT);
            DrawTextW(hdc, &mut stamp, &mut src, DT_SINGLELINE | DT_VCENTER | DT_LEFT | DT_END_ELLIPSIS);

            // Duration + level range, right-aligned columns.
            let mut dur: Vec<u16> = item.dur.encode_utf16().collect();
            let mut drc = RECT {
                left: rc.right - (220.0 * s).round() as i32,
                top: rc.top,
                right: rc.right - (110.0 * s).round() as i32,
                bottom: rc.bottom,
            };
            let _ = SetTextColor(hdc, if item.charge { C_GREEN } else { C_TEXT });
            DrawTextW(hdc, &mut dur, &mut drc, DT_SINGLELINE | DT_VCENTER | DT_RIGHT | DT_END_ELLIPSIS);
            let mut lvl: Vec<u16> = item.levels.encode_utf16().collect();
            let mut lrc = RECT {
                left: rc.right - (100.0 * s).round() as i32,
                top: rc.top,
                right: rc.right - pad,
                bottom: rc.bottom,
            };
            let _ = SetTextColor(hdc, C_TEXT2);
            DrawTextW(hdc, &mut lvl, &mut lrc, DT_SINGLELINE | DT_VCENTER | DT_RIGHT | DT_END_ELLIPSIS);
        }
        _ => {}
    }
}

/// Level-over-time line on a card: green over charging, white while
/// discharging, dark bands (and no line) where the device was off/unknown —
/// the auto-shutdown time excluded from usage stats is visible as a gap.
/// Swap jumps get an orange dot.
fn draw_chart(hdc: HDC, v: &ViewerState, chart: RECT, s: f32) {
    unsafe {
        draw_card(hdc, chart, s);
        let pad = (12.0 * s).round() as i32;
        // Card header: caption + legend chips.
        let mut cap: Vec<u16> = "Battery level".encode_utf16().collect();
        let mut crc = RECT {
            left: chart.left + pad,
            top: chart.top + (8.0 * s).round() as i32,
            right: chart.right - pad - (200.0 * s).round() as i32,
            bottom: chart.top + (24.0 * s).round() as i32,
        };
        let _ = SelectObject(hdc, CAPTION_FONT);
        let _ = SetTextColor(hdc, C_TEXT2);
        DrawTextW(hdc, &mut cap, &mut crc, DT_SINGLELINE | DT_LEFT | DT_END_ELLIPSIS);

        // Plot rect inside the card, leaving room for header + time labels.
        let plot = RECT {
            left: chart.left + pad + (26.0 * s).round() as i32,
            top: chart.top + (30.0 * s).round() as i32,
            right: chart.right - pad,
            bottom: chart.bottom - (24.0 * s).round() as i32,
        };
        if plot.right - plot.left < 40 || plot.bottom - plot.top < 30 {
            return;
        }

        if v.samples.len() < 2 {
            let mut text: Vec<u16> =
                "No data yet — recording starts when a device connects.".encode_utf16().collect();
            let mut trc = chart;
            let _ = SelectObject(hdc, BODY_FONT);
            let _ = SetTextColor(hdc, C_TEXT3);
            DrawTextW(hdc, &mut text, &mut trc, DT_SINGLELINE | DT_CENTER | DT_VCENTER);
            return;
        }
        let t0 = v.samples[0].ts;
        let t1 = v.samples[v.samples.len() - 1].ts;
        let span_t = (t1 - t0).max(1) as f64;
        let width = (plot.right - plot.left) as f64;
        let x_for = |ts: i64| plot.left + (((ts - t0) as f64 / span_t) * width) as i32;
        let y_for = |level: u8| {
            plot.bottom - ((level.min(100) as f32 / 100.0) * (plot.bottom - plot.top) as f32) as i32
        };

        // Dotted grid at 0/50/100 + labels.
        let grid_pen = CreatePen(PS_DOT, 1, C_GRID);
        let old_pen = SelectObject(hdc, grid_pen);
        let _ = SelectObject(hdc, CAPTION_FONT);
        for (lv, label) in [(100u8, "100"), (50u8, "50"), (0u8, "0")] {
            let y = y_for(lv);
            let _ = MoveToEx(hdc, plot.left, y, None);
            let _ = LineTo(hdc, plot.right, y);
            let mut label: Vec<u16> = label.encode_utf16().collect();
            let mut lrc = RECT {
                left: plot.left - (24.0 * s).round() as i32,
                top: y - (8.0 * s).round() as i32,
                right: plot.left - (4.0 * s).round() as i32,
                bottom: y + (8.0 * s).round() as i32,
            };
            let _ = SetTextColor(hdc, C_TEXT3);
            DrawTextW(hdc, &mut label, &mut lrc, DT_SINGLELINE | DT_RIGHT | DT_VCENTER);
        }
        let _ = SelectObject(hdc, old_pen);
        let _ = DeleteObject(grid_pen);

        // Bands (under the line): charging pale green, off/unknown darker.
        for i in 1..v.samples.len() {
            let a = &v.samples[i - 1];
            let b = &v.samples[i];
            let gap = b.ts - a.ts;
            let xa = x_for(a.ts);
            let xb = x_for(b.ts).max(xa + 1);
            let color = if !b.connected || gap > history::GAP_BREAK_SECS {
                Some(C_OFF_BAND)
            } else if b.charging {
                Some(C_GREEN_BAND)
            } else {
                None
            };
            if let Some(color) = color {
                let brush = CreateSolidBrush(color);
                let band = RECT { left: xa, top: plot.top, right: xb, bottom: plot.bottom };
                let _ = FillRect(hdc, &band, brush);
                let _ = DeleteObject(brush);
            }
        }

        // Area fill under contiguous discharge runs (subtle depth), then the
        // line itself: white for discharge, green for charging.
        let dis_pen = CreatePen(PS_SOLID, 2, C_LINE);
        let chg_pen = CreatePen(PS_SOLID, 2, C_GREEN);
        let area_brush = CreateSolidBrush(C_AREA);
        let null_pen = CreatePen(PS_NULL, 0, COLORREF(0));
        let mut run: Vec<POINT> = Vec::new();
        let flush_run = |run: &mut Vec<POINT>| {
            if run.len() >= 2 {
                let mut poly = run.clone();
                let last_x = poly.last().unwrap().x;
                poly.push(POINT { x: last_x, y: plot.bottom });
                poly.push(POINT { x: run[0].x, y: plot.bottom });
                let old_pen = SelectObject(hdc, null_pen);
                let old_brush = SelectObject(hdc, area_brush);
                let _ = Polygon(hdc, &poly);
                let _ = SelectObject(hdc, old_pen);
                let _ = SelectObject(hdc, old_brush);
            }
            run.clear();
        };
        for i in 1..v.samples.len() {
            let a = &v.samples[i - 1];
            let b = &v.samples[i];
            let gap = b.ts - a.ts;
            let connected_run = b.connected && a.connected && gap <= history::GAP_BREAK_SECS;
            if !connected_run {
                flush_run(&mut run);
                continue;
            }
            if run.is_empty() {
                run.push(POINT { x: x_for(a.ts), y: y_for(a.level) });
            }
            run.push(POINT { x: x_for(b.ts), y: y_for(b.level) });
        }
        flush_run(&mut run);

        let mut cur_pen = old_pen;
        for i in 1..v.samples.len() {
            let a = &v.samples[i - 1];
            let b = &v.samples[i];
            let gap = b.ts - a.ts;
            if !b.connected || !a.connected || gap > history::GAP_BREAK_SECS {
                continue;
            }
            let pen = if b.charging { chg_pen } else { dis_pen };
            if cur_pen.0 != pen.0 {
                cur_pen = SelectObject(hdc, pen);
            }
            let _ = MoveToEx(hdc, x_for(a.ts), y_for(a.level), None);
            let _ = LineTo(hdc, x_for(b.ts), y_for(b.level));
        }
        let _ = SelectObject(hdc, old_pen);
        let _ = DeleteObject(dis_pen);
        let _ = DeleteObject(chg_pen);
        let _ = DeleteObject(area_brush);
        let _ = DeleteObject(null_pen);

        // Swap jumps: orange dot at the jumped-up sample.
        for i in 1..v.samples.len() {
            let a = &v.samples[i - 1];
            let b = &v.samples[i];
            if !a.charging && !b.charging && b.level as i32 - a.level as i32 >= 30 {
                let cx = x_for(b.ts);
                let cy = y_for(b.level);
                let r = (4.0 * s).round() as i32;
                let brush = CreateSolidBrush(C_ORANGE);
                let old = SelectObject(hdc, brush);
                let _ = Ellipse(hdc, cx - r, cy - r, cx + r, cy + r);
                let _ = SelectObject(hdc, old);
                let _ = DeleteObject(brush);
            }
        }

        // Time labels: 5 evenly spaced ticks.
        let _ = SelectObject(hdc, CAPTION_FONT);
        for k in 0..5i64 {
            let ts = t0 + (t1 - t0) * k / 4;
            let stamp = fmt_stamp(ts);
            let mut label: Vec<u16> = stamp.encode_utf16().collect();
            let x = plot.left + ((plot.right - plot.left) * k as i32 / 4);
            let align = match k {
                0 => DT_LEFT,
                4 => DT_RIGHT,
                _ => DT_CENTER,
            };
            let mut trc = RECT {
                left: if k == 0 { plot.left - (24.0 * s).round() as i32 } else { x - (70.0 * s).round() as i32 },
                top: plot.bottom + (4.0 * s).round() as i32,
                right: if k == 4 { plot.right } else { x + (70.0 * s).round() as i32 },
                bottom: plot.bottom + (22.0 * s).round() as i32,
            };
            let _ = SetTextColor(hdc, C_TEXT3);
            DrawTextW(hdc, &mut label, &mut trc, DT_SINGLELINE | align);
        }

        // Legend chips at the card's top-right.
        let legend: [(&str, COLORREF); 3] =
            [("charging", C_GREEN), ("discharging", C_LINE), ("off (excluded)", C_TEXT3)];
        let mut lx = chart.right - pad;
        let ly = chart.top + (14.0 * s).round() as i32;
        let _ = SelectObject(hdc, CAPTION_FONT);
        for (label, color) in legend.iter().rev() {
            let mut text: Vec<u16> = label.encode_utf16().collect();
            let mut trc = RECT { left: 0, top: 0, right: 0, bottom: 0 };
            DrawTextW(hdc, &mut text.clone(), &mut trc, DT_SINGLELINE | DT_CALCRECT);
            let tw = trc.right - trc.left;
            lx -= tw;
            let mut trc = RECT { left: lx, top: ly - (7.0 * s).round() as i32, right: lx + tw, bottom: ly + (8.0 * s).round() as i32 };
            let _ = SetTextColor(hdc, C_TEXT2);
            DrawTextW(hdc, &mut text, &mut trc, DT_SINGLELINE | DT_LEFT);
            lx -= (12.0 * s).round() as i32;
            let brush = CreateSolidBrush(*color);
            let old = SelectObject(hdc, brush);
            let _ = Ellipse(hdc, lx, ly - (3.0 * s).round() as i32, lx + (6.0 * s).round() as i32, ly + (3.0 * s).round() as i32);
            let _ = SelectObject(hdc, old);
            let _ = DeleteObject(brush);
            lx -= (16.0 * s).round() as i32;
        }
    }
}

fn create_font(height: i32, weight: i32, face: PCWSTR) -> HFONT {
    unsafe {
        CreateFontW(
            height,
            0,
            0,
            0,
            weight,
            0,
            0,
            0,
            DEFAULT_CHARSET.0 as u32,
            OUT_DEFAULT_PRECIS.0 as u32,
            CLIP_DEFAULT_PRECIS.0 as u32,
            CLEARTYPE_QUALITY.0 as u32,
            (DEFAULT_PITCH.0 | FF_DONTCARE.0) as u32,
            face,
        )
    }
}

fn dpi_scale(hwnd: HWND) -> f32 {
    unsafe {
        let dpi = GetDpiForWindow(hwnd);
        if dpi == 0 {
            1.0
        } else {
            dpi as f32 / 96.0
        }
    }
}

fn unix_now() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

/// Unix seconds → FILETIME ticks (100ns since 1601-01-01). Pure, tested.
fn unix_to_filetime_ticks(ts: i64) -> Option<u64> {
    let secs = ts.checked_add(11_644_473_600)?;
    if secs < 0 {
        return None;
    }
    Some(secs as u64 * 10_000_000)
}

/// Local-time parts via FILETIME conversion (no chrono dependency).
fn local_parts(ts: i64) -> Option<(u16, u16, u16, u16, u16)> {
    unsafe {
        let ticks = unix_to_filetime_ticks(ts)?;
        let ft = FILETIME { dwLowDateTime: ticks as u32, dwHighDateTime: (ticks >> 32) as u32 };
        let mut local = FILETIME::default();
        FileTimeToLocalFileTime(&ft, &mut local).ok()?;
        let mut st = SYSTEMTIME::default();
        FileTimeToSystemTime(&local, &mut st).ok()?;
        Some((st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute))
    }
}

fn fmt_parts(y: u16, mo: u16, d: u16, h: u16, mi: u16) -> String {
    format!("{y:04}-{mo:02}-{d:02} {h:02}:{mi:02}")
}

fn fmt_stamp(ts: i64) -> String {
    match local_parts(ts) {
        Some((y, mo, d, h, mi)) => fmt_parts(y, mo, d, h, mi),
        None => "--".into(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn filetime_conversion_epoch() {
        // 1970-01-01 UTC = 11644473600e7 ticks since 1601-01-01.
        assert_eq!(unix_to_filetime_ticks(0), Some(116_444_736_000_000_000));
        let ticks = unix_to_filetime_ticks(1_000_000_000).unwrap();
        assert_eq!(ticks, (11_644_473_600i64 + 1_000_000_000) as u64 * 10_000_000);
        // Before 1601-01-01 there is no FILETIME representation.
        assert_eq!(unix_to_filetime_ticks(-11_644_473_601), None);
    }

    #[test]
    fn stamp_formatting() {
        assert_eq!(fmt_parts(2026, 9, 5, 14, 5), "2026-09-05 14:05");
        assert_eq!(fmt_parts(1999, 12, 31, 23, 59), "1999-12-31 23:59");
    }
}
