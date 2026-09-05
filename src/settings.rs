//! "Settings" window consolidating every option that used to live only in
//! the tray right-click menu (displayed device, widget side, overlap/hover/
//! tray toggles, poll + record intervals, language, autostart). Dark Win11
//! chrome mirrors `viewer.rs`: DWM immersive dark title bar + rounded
//! corners, WinUI palette, card sections, owner-drawn pills.
//!
//! Single instance, lives on the UI thread (same message loop as the
//! widget), so handlers call straight into the `window.rs` helpers and every
//! change applies + persists immediately — no OK/Cancel. Consumers pick the
//! change up on their own cadence (watcher re-reads settings.json each
//! cycle, history per sample); the explicit side effects here only cover
//! UI-thread surfaces (tray icon create/destroy, hover hide, re-place).

#![allow(static_mut_refs)]

use std::iter::once;

use windows::core::{w, PCWSTR};
use windows::Win32::Foundation::*;
use windows::Win32::Graphics::Dwm::{
    DwmSetWindowAttribute, DWMWA_USE_IMMERSIVE_DARK_MODE, DWMWA_WINDOW_CORNER_PREFERENCE,
};
use windows::Win32::Graphics::Gdi::*;
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::Controls::{SetWindowTheme, BST_CHECKED, DRAWITEMSTRUCT, ODS_SELECTED};
use windows::Win32::UI::HiDpi::GetDpiForWindow;
use windows::Win32::UI::WindowsAndMessaging::*;

const CLASS_NAME: PCWSTR = w!("RazerTaskbarSettings");

const IDC_SIDE_LEFT: i32 = 1;
const IDC_SIDE_RIGHT: i32 = 2;
const IDC_CHK_EST: i32 = 3;
const IDC_CHK_TRAY: i32 = 5;
const IDC_CHK_HOVER: i32 = 6;
const IDC_CHK_REC: i32 = 7;
const IDC_CHK_AUTOSTART: i32 = 8;
const IDC_COMBO_DEVICE: i32 = 9;
const IDC_COMBO_POLL: i32 = 10;
const IDC_COMBO_RECI: i32 = 11;
const IDC_COMBO_LANG: i32 = 12;

// WinUI dark palette — same values as viewer.rs (COLORREF is 0x00BBGGRR).
const C_BG: COLORREF = COLORREF(0x001F1F1F);
const C_CARD: COLORREF = COLORREF(0x002B2B2B);
const C_CARD_BORDER: COLORREF = COLORREF(0x00383838);
const C_TEXT: COLORREF = COLORREF(0x00F3F3F3);
const C_TEXT2: COLORREF = COLORREF(0x00ACACAC);
const C_ACCENT: COLORREF = COLORREF(0x00FFCD60); // #60CDFF
const C_ACCENT_TEXT: COLORREF = COLORREF(0x005C3A00); // #003A5C on accent
const C_ROW_SEL: COLORREF = COLORREF(0x003A3A3A);

// Fixed-page layout in DIPs at 96. The window is not resizable, so geometry
// is computed from one scale and shared by control creation and WM_PAINT.
const CLIENT_W: f32 = 470.0;
const MARGIN: f32 = 16.0;
const SECTION_GAP: f32 = 14.0;
const HEADER_H: f32 = 24.0;
const CARD_PAD: f32 = 14.0;
const ROW_CHECK: f32 = 26.0;
const ROW_CONTROL: f32 = 30.0;
const LABEL_W: f32 = 118.0;
const PILL_W: f32 = 88.0;

const POLL_CHOICES: [u64; 5] = [5, 10, 15, 30, 60];
const REC_CHOICES: [u64; 5] = [1, 2, 5, 10, 30];
const LANG_KEYS: [&str; 3] = ["auto", "en", "zh"];
const LANG_LABELS: [&str; 3] = ["Auto", "English", "中文"];

/// One row inside a section card. Checkbox captions live in the controls
/// themselves; labeled rows get a painted label plus a control slot (a plain
/// combobox, or the Left/Right pill pair for `pills`).
#[derive(Clone, Copy)]
enum Row {
    Labeled {
        label: &'static str,
        id: i32,
        pills: bool,
    },
    Check(i32),
}

fn sections() -> Vec<(&'static str, Vec<Row>)> {
    vec![
        (
            "Widget",
            vec![
                Row::Labeled {
                    label: "Shown device",
                    id: IDC_COMBO_DEVICE,
                    pills: false,
                },
                Row::Labeled {
                    label: "Widget side",
                    id: IDC_SIDE_LEFT,
                    pills: true,
                },
                Row::Check(IDC_CHK_EST),
                Row::Check(IDC_CHK_TRAY),
                Row::Check(IDC_CHK_HOVER),
            ],
        ),
        (
            "History",
            vec![
                Row::Labeled {
                    label: "Poll interval",
                    id: IDC_COMBO_POLL,
                    pills: false,
                },
                Row::Check(IDC_CHK_REC),
                Row::Labeled {
                    label: "Record interval",
                    id: IDC_COMBO_RECI,
                    pills: false,
                },
            ],
        ),
        (
            "General",
            vec![
                Row::Labeled {
                    label: "Language",
                    id: IDC_COMBO_LANG,
                    pills: false,
                },
                Row::Check(IDC_CHK_AUTOSTART),
            ],
        ),
    ]
}

fn check_caption(id: i32) -> &'static str {
    match id {
        IDC_CHK_EST => "Show time remaining on widget",
        IDC_CHK_TRAY => "Show tray icon",
        IDC_CHK_HOVER => "Show devices on hover",
        IDC_CHK_REC => "Record battery history",
        IDC_CHK_AUTOSTART => "Run at startup",
        _ => "",
    }
}

struct RowGeom {
    row: Row,
    label: Option<RECT>,
    control: RECT,
}

struct SectionGeom {
    title: &'static str,
    card: RECT,
    rows: Vec<RowGeom>,
}

fn px(v: f32, s: f32) -> i32 {
    (v * s).round() as i32
}

fn row_height(r: &Row, row_check: i32, row_ctl: i32) -> i32 {
    match r {
        Row::Check(_) => row_check,
        Row::Labeled { .. } => row_ctl,
    }
}

/// Page geometry: design client rect + per-section card/label/control rects,
/// in one place so control creation and WM_PAINT never drift apart.
fn geometry(s: f32) -> (RECT, Vec<SectionGeom>) {
    let m = px(MARGIN, s);
    let gap = px(SECTION_GAP, s);
    let header_h = px(HEADER_H, s);
    let pad = px(CARD_PAD, s);
    let row_check = px(ROW_CHECK, s);
    let row_ctl = px(ROW_CONTROL, s);
    let label_w = px(LABEL_W, s);
    let label_gap = px(10.0, s);
    let client_w = px(CLIENT_W, s);

    let mut y = m;
    let mut out = Vec::new();
    for (title, rows) in sections() {
        y += header_h; // caption sits above the card
        let inner_h: i32 = rows.iter().map(|r| row_height(r, row_check, row_ctl)).sum();
        let card = RECT {
            left: m,
            top: y,
            right: client_w - m,
            bottom: y + inner_h + pad * 2,
        };
        let mut geoms = Vec::new();
        let mut ry = card.top + pad;
        for r in rows {
            let h = row_height(&r, row_check, row_ctl);
            let labeled = matches!(r, Row::Labeled { .. });
            geoms.push(RowGeom {
                row: r,
                label: labeled.then(|| RECT {
                    left: card.left + pad,
                    top: ry,
                    right: card.left + pad + label_w,
                    bottom: ry + h,
                }),
                control: RECT {
                    left: card.left + pad + if labeled { label_w + label_gap } else { 0 },
                    top: ry,
                    right: card.right - pad,
                    bottom: ry + h,
                },
            });
            ry += h;
        }
        out.push(SectionGeom {
            title,
            card,
            rows: geoms,
        });
        y = card.bottom + gap;
    }
    (
        RECT {
            left: 0,
            top: 0,
            right: client_w,
            bottom: y - gap + m,
        },
        out,
    )
}

/// The child windows, keyed the same way as the `IDC_*` constants.
struct Controls {
    side_left: HWND,
    side_right: HWND,
    chk_est: HWND,
    chk_tray: HWND,
    chk_hover: HWND,
    chk_rec: HWND,
    chk_autostart: HWND,
    combo_device: HWND,
    combo_poll: HWND,
    combo_reci: HWND,
    combo_lang: HWND,
}

struct SettingsState {
    hwnd: HWND,
    ctl: Controls,
}

// UI-thread-only state (same pattern as window.rs STATE / viewer.rs VIEWER).
static mut SETTINGS: Option<SettingsState> = None;
static mut CLASS_OK: bool = false;
static mut BODY_FONT: HFONT = HFONT(std::ptr::null_mut());
static mut CAPTION_FONT: HFONT = HFONT(std::ptr::null_mut());
static mut CARD_BRUSH: HBRUSH = HBRUSH(std::ptr::null_mut());

/// Open (or focus + re-sync) the settings window.
pub fn open() {
    unsafe {
        if let Some(v) = SETTINGS.as_ref() {
            refresh_controls();
            let _ = ShowWindow(v.hwnd, SW_SHOW);
            let _ = SetForegroundWindow(v.hwnd);
            return;
        }
        register_class();
        let instance = GetModuleHandleW(None).unwrap_or_default();
        let title: Vec<u16> = crate::i18n::tr("Settings — Razer Taskbar")
            .encode_utf16()
            .chain(once(0))
            .collect();
        // Fixed size (no thick frame): rough size here, exact outer rect
        // below once the DPI of the hosting monitor is known.
        let hwnd = CreateWindowExW(
            WINDOW_EX_STYLE(0),
            CLASS_NAME,
            PCWSTR(title.as_ptr()),
            WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX,
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            500,
            620,
            None,
            None,
            instance,
            None,
        )
        .unwrap_or(HWND(std::ptr::null_mut()));
        if hwnd.0.is_null() {
            eprintln!("razer-taskbar: settings window create failed");
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
        if BODY_FONT.0.is_null() {
            BODY_FONT = create_font((14.0 * s).round() as i32, 400, w!("Segoe UI Variable Text"));
            CAPTION_FONT =
                create_font((12.0 * s).round() as i32, 400, w!("Segoe UI Variable Text"));
            CARD_BRUSH = CreateSolidBrush(C_CARD);
        }

        let (client, geoms) = geometry(s);
        let style = WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
        let mut outer = RECT {
            left: 0,
            top: 0,
            right: client.right,
            bottom: client.bottom,
        };
        let _ = AdjustWindowRectEx(&mut outer, style, false, WINDOW_EX_STYLE(0));
        let _ = SetWindowPos(
            hwnd,
            HWND(std::ptr::null_mut()),
            0,
            0,
            outer.right - outer.left,
            outer.bottom - outer.top,
            SWP_NOMOVE | SWP_NOZORDER,
        );

        let ctl = create_controls(hwnd, &geoms, instance.into(), s);
        SETTINGS = Some(SettingsState { hwnd, ctl });
        refresh_controls();
        let _ = ShowWindow(hwnd, SW_SHOW);
    }
}

/// Close the settings window if open (main window WM_DESTROY).
pub fn destroy() {
    unsafe {
        if let Some(v) = SETTINGS.take() {
            let _ = DestroyWindow(v.hwnd);
        }
    }
}

/// Re-localize the open window after a language switch: title, checkbox
/// captions, device combo's "All devices" entry; painted captions/labels and
/// the pills repaint via the invalidations in `refresh_controls`.
pub fn sync_language() {
    unsafe {
        let Some(v) = SETTINGS.as_ref() else { return };
        let title: Vec<u16> = crate::i18n::tr("Settings — Razer Taskbar")
            .encode_utf16()
            .chain(once(0))
            .collect();
        let _ = SetWindowTextW(v.hwnd, PCWSTR(title.as_ptr()));
        refresh_controls();
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

fn create_controls(hwnd: HWND, geoms: &[SectionGeom], instance: HINSTANCE, s: f32) -> Controls {
    unsafe {
        let mut ctl = Controls {
            side_left: HWND(std::ptr::null_mut()),
            side_right: HWND(std::ptr::null_mut()),
            chk_est: HWND(std::ptr::null_mut()),
            chk_tray: HWND(std::ptr::null_mut()),
            chk_hover: HWND(std::ptr::null_mut()),
            chk_rec: HWND(std::ptr::null_mut()),
            chk_autostart: HWND(std::ptr::null_mut()),
            combo_device: HWND(std::ptr::null_mut()),
            combo_poll: HWND(std::ptr::null_mut()),
            combo_reci: HWND(std::ptr::null_mut()),
            combo_lang: HWND(std::ptr::null_mut()),
        };
        let mk_check = |id: i32, rc: RECT| {
            let wide: Vec<u16> = crate::i18n::tr(check_caption(id))
                .encode_utf16()
                .chain(once(0))
                .collect();
            let h = CreateWindowExW(
                WINDOW_EX_STYLE(0),
                w!("BUTTON"),
                PCWSTR(wide.as_ptr()),
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WINDOW_STYLE(BS_AUTOCHECKBOX as u32),
                rc.left,
                rc.top,
                rc.right - rc.left,
                rc.bottom - rc.top,
                hwnd,
                HMENU(id as usize as *mut core::ffi::c_void),
                instance,
                None,
            )
            .unwrap_or_default();
            // Dark glyph + dark dropdown list (Win10 1809+; best effort).
            let _ = SetWindowTheme(h, w!("DarkMode_Explorer"), PCWSTR::null());
            let _ = SendMessageW(h, WM_SETFONT, WPARAM(BODY_FONT.0 as usize), LPARAM(1));
            h
        };
        let mk_combo = |id: i32, rc: RECT| {
            let h = CreateWindowExW(
                WINDOW_EX_STYLE(0),
                w!("COMBOBOX"),
                PCWSTR::null(),
                WS_CHILD
                    | WS_VISIBLE
                    | WS_TABSTOP
                    | WS_VSCROLL
                    | WINDOW_STYLE(CBS_DROPDOWNLIST as u32),
                rc.left,
                rc.top,
                rc.right - rc.left,
                px(240.0, s), // dropdown list height
                hwnd,
                HMENU(id as usize as *mut core::ffi::c_void),
                instance,
                None,
            )
            .unwrap_or_default();
            let _ = SetWindowTheme(h, w!("DarkMode_Explorer"), PCWSTR::null());
            let _ = SendMessageW(h, WM_SETFONT, WPARAM(BODY_FONT.0 as usize), LPARAM(1));
            h
        };

        for g in geoms {
            for rg in &g.rows {
                let rc = rg.control;
                match rg.row {
                    Row::Check(id) => {
                        let h = mk_check(id, rc);
                        match id {
                            IDC_CHK_EST => ctl.chk_est = h,
                            IDC_CHK_TRAY => ctl.chk_tray = h,
                            IDC_CHK_HOVER => ctl.chk_hover = h,
                            IDC_CHK_REC => ctl.chk_rec = h,
                            IDC_CHK_AUTOSTART => ctl.chk_autostart = h,
                            _ => {}
                        }
                    }
                    Row::Labeled { id, pills, .. } => {
                        if pills {
                            let pw = px(PILL_W, s);
                            let step = pw + px(8.0, s);
                            for (pid, key, x) in [
                                (IDC_SIDE_LEFT, "Left", rc.left),
                                (IDC_SIDE_RIGHT, "Right", rc.left + step),
                            ] {
                                let wide: Vec<u16> =
                                    crate::i18n::tr(key).encode_utf16().chain(once(0)).collect();
                                let h = CreateWindowExW(
                                    WINDOW_EX_STYLE(0),
                                    w!("BUTTON"),
                                    PCWSTR(wide.as_ptr()),
                                    WS_CHILD
                                        | WS_VISIBLE
                                        | WS_TABSTOP
                                        | WINDOW_STYLE(BS_OWNERDRAW as u32),
                                    x,
                                    rc.top,
                                    pw,
                                    rc.bottom - rc.top,
                                    hwnd,
                                    HMENU(pid as usize as *mut core::ffi::c_void),
                                    instance,
                                    None,
                                )
                                .unwrap_or_default();
                                if pid == IDC_SIDE_LEFT {
                                    ctl.side_left = h;
                                } else {
                                    ctl.side_right = h;
                                }
                            }
                        } else {
                            let h = mk_combo(id, rc);
                            match id {
                                IDC_COMBO_DEVICE => ctl.combo_device = h,
                                IDC_COMBO_POLL => ctl.combo_poll = h,
                                IDC_COMBO_RECI => ctl.combo_reci = h,
                                IDC_COMBO_LANG => ctl.combo_lang = h,
                                _ => {}
                            }
                        }
                    }
                }
            }
        }
        ctl
    }
}

/// Connected devices sorted by name — the device combo's entries 1.. (entry
/// 0 is "All devices"), matching the tray menu's list.
fn connected_sorted() -> Vec<(String, String)> {
    let Some(devices) = crate::window::devices_arc() else {
        return Vec::new();
    };
    let devices = devices.lock().unwrap();
    let mut connected: Vec<_> = devices.values().filter(|d| d.is_connected).collect();
    connected.sort_by(|a, b| a.name.cmp(&b.name));
    connected
        .iter()
        .map(|d| (d.handle.clone(), d.name.clone()))
        .collect()
}

/// Push the current config + device list into the controls. Runs on open and
/// after a language switch; programmatic BM_SETCHECK / CB_SETCURSEL do not
/// fire BN_CLICKED / CBN_SELCHANGE, so no echo guard is needed.
fn refresh_controls() {
    unsafe {
        let Some(v) = SETTINGS.as_ref() else { return };
        let c = &v.ctl;
        let cfg = crate::window::config_snapshot();
        let set_chk = |h: HWND, on: bool| {
            let _ = SendMessageW(
                h,
                BM_SETCHECK,
                WPARAM(if on { BST_CHECKED.0 as usize } else { 0 }),
                LPARAM(0),
            );
        };
        set_chk(c.chk_est, cfg.show_estimated_time);
        set_chk(c.chk_tray, cfg.show_tray_icon);
        set_chk(c.chk_hover, cfg.hover_devices);
        set_chk(c.chk_rec, cfg.record_battery_history);
        set_chk(c.chk_autostart, cfg.run_at_startup);

        let fill_combo = |h: HWND, items: &[String], idx: usize| {
            let _ = SendMessageW(h, CB_RESETCONTENT, WPARAM(0), LPARAM(0));
            for it in items {
                let wide: Vec<u16> = it.encode_utf16().chain(once(0)).collect();
                let _ = SendMessageW(h, CB_ADDSTRING, WPARAM(0), LPARAM(wide.as_ptr() as isize));
            }
            let _ = SendMessageW(h, CB_SETCURSEL, WPARAM(idx), LPARAM(0));
        };
        fill_combo(
            c.combo_poll,
            &POLL_CHOICES
                .iter()
                .map(|s| format!("{s}s"))
                .collect::<Vec<_>>(),
            POLL_CHOICES
                .iter()
                .position(|&s| s == cfg.polling_throttle_secs)
                .unwrap_or(2),
        );
        fill_combo(
            c.combo_reci,
            &REC_CHOICES
                .iter()
                .map(|s| format!("{s}s"))
                .collect::<Vec<_>>(),
            REC_CHOICES
                .iter()
                .position(|&s| s == cfg.history_poll_interval_secs)
                .unwrap_or(2),
        );
        // Refreshed here too so a language switch relocalizes "Auto".
        fill_combo(
            c.combo_lang,
            &LANG_LABELS
                .iter()
                .map(|k| crate::i18n::tr(k).to_string())
                .collect::<Vec<_>>(),
            LANG_KEYS
                .iter()
                .position(|k| *k == cfg.language)
                .unwrap_or(0),
        );

        let devices = connected_sorted();
        let _ = SendMessageW(c.combo_device, CB_RESETCONTENT, WPARAM(0), LPARAM(0));
        let all: Vec<u16> = crate::i18n::tr("All devices")
            .encode_utf16()
            .chain(once(0))
            .collect();
        let _ = SendMessageW(
            c.combo_device,
            CB_ADDSTRING,
            WPARAM(0),
            LPARAM(all.as_ptr() as isize),
        );
        let mut sel = 0usize;
        for (i, (handle, name)) in devices.iter().enumerate() {
            let wide: Vec<u16> = format!("{name} — {}", pct_of(handle))
                .encode_utf16()
                .chain(once(0))
                .collect();
            let _ = SendMessageW(
                c.combo_device,
                CB_ADDSTRING,
                WPARAM(0),
                LPARAM(wide.as_ptr() as isize),
            );
            if !cfg.shown_device_handle.is_empty() && cfg.shown_device_handle == *handle {
                sel = i + 1;
            }
        }
        let _ = SendMessageW(c.combo_device, CB_SETCURSEL, WPARAM(sel), LPARAM(0));

        // Pills read the config at draw time; labels/captions are painted.
        let _ = InvalidateRect(c.side_left, None, false);
        let _ = InvalidateRect(c.side_right, None, false);
        let _ = InvalidateRect(v.hwnd, None, false);
    }
}

/// Live battery percentage for a handle (combo label), mirroring the menu's
/// `Name — N%` format.
fn pct_of(handle: &str) -> String {
    let Some(devices) = crate::window::devices_arc() else {
        return "--".into();
    };
    let devices = devices.lock().unwrap();
    match devices.values().find(|d| d.handle == handle) {
        Some(d) => format!("{}%", d.battery_percentage),
        None => "--".into(),
    }
}

unsafe extern "system" fn wnd_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    match msg {
        WM_COMMAND => {
            let id = (wparam.0 & 0xFFFF) as u16 as i32;
            let code = (wparam.0 >> 16) as u16 as i32;
            handle_command(id, code);
            LRESULT(0)
        }
        WM_DRAWITEM => {
            let dis = &mut *(lparam.0 as *mut DRAWITEMSTRUCT);
            draw_item(dis);
            LRESULT(1)
        }
        // Checkbox text and the combobox display/list: dark text on the card
        // color. (Check boxes and radio buttons send WM_CTLCOLORSTATIC.)
        WM_CTLCOLORSTATIC | WM_CTLCOLORLISTBOX => {
            let hdc = HDC(wparam.0 as *mut core::ffi::c_void);
            let _ = SetTextColor(hdc, C_TEXT);
            let _ = SetBkMode(hdc, TRANSPARENT);
            LRESULT(CARD_BRUSH.0 as isize)
        }
        WM_PAINT => {
            paint(hwnd);
            LRESULT(0)
        }
        WM_DESTROY => {
            SETTINGS = None;
            LRESULT(0)
        }
        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}

fn combo_index(h: HWND) -> usize {
    unsafe {
        let idx = SendMessageW(h, CB_GETCURSEL, WPARAM(0), LPARAM(0)).0;
        if idx < 0 {
            0
        } else {
            idx as usize
        }
    }
}

fn handle_command(id: i32, code: i32) {
    unsafe {
        let Some(v) = SETTINGS.as_ref() else { return };
        let c = &v.ctl;
        let clicked = code == BN_CLICKED as i32;
        let selchange = code == CBN_SELCHANGE as i32;
        match id {
            IDC_COMBO_DEVICE if selchange => {
                let idx = combo_index(c.combo_device);
                let handle = if idx == 0 {
                    String::new()
                } else {
                    connected_sorted()
                        .get(idx - 1)
                        .map(|(h, _)| h.clone())
                        .unwrap_or_default()
                };
                crate::window::set_shown_device(&handle);
            }
            IDC_COMBO_POLL if selchange => {
                if let Some(&secs) = POLL_CHOICES.get(combo_index(c.combo_poll)) {
                    crate::window::modify_config(|cfg| cfg.polling_throttle_secs = secs);
                }
            }
            IDC_COMBO_RECI if selchange => {
                if let Some(&secs) = REC_CHOICES.get(combo_index(c.combo_reci)) {
                    crate::window::modify_config(|cfg| cfg.history_poll_interval_secs = secs);
                }
            }
            IDC_COMBO_LANG if selchange => {
                let idx = combo_index(c.combo_lang);
                if let Some(&key) = LANG_KEYS.get(idx) {
                    crate::window::modify_config(|cfg| cfg.language = key.into());
                    crate::i18n::set_setting(idx as u8);
                    crate::tray::refresh();
                    crate::viewer::sync_language();
                    sync_language();
                }
            }
            IDC_SIDE_LEFT if clicked => set_side(true),
            IDC_SIDE_RIGHT if clicked => set_side(false),
            IDC_CHK_EST if clicked => {
                let on = checked(c.chk_est);
                crate::window::modify_config(|cfg| cfg.show_estimated_time = on);
                // The widget width depends on the toggle.
                crate::window::reposition_widget();
            }
            IDC_CHK_TRAY if clicked => {
                let on = checked(c.chk_tray);
                crate::window::modify_config(|cfg| cfg.show_tray_icon = on);
                if on {
                    if let Some(devices) = crate::window::devices_arc() {
                        // Re-bind the device map too: when the icon was
                        // disabled at startup, set_devices never ran and the
                        // tooltip would stay generic forever.
                        crate::tray::set_devices(devices);
                        let hwnd = crate::window::widget_hwnd();
                        if !hwnd.0.is_null() {
                            crate::tray::ensure_created(hwnd);
                        }
                    }
                } else {
                    crate::tray::destroy();
                }
            }
            IDC_CHK_HOVER if clicked => {
                let on = checked(c.chk_hover);
                crate::window::modify_config(|cfg| cfg.hover_devices = on);
                if !on {
                    crate::hover::hide();
                }
            }
            IDC_CHK_REC if clicked => {
                let on = checked(c.chk_rec);
                crate::window::modify_config(|cfg| cfg.record_battery_history = on);
            }
            IDC_CHK_AUTOSTART if clicked => {
                let on = checked(c.chk_autostart);
                crate::window::modify_config(|cfg| cfg.run_at_startup = on);
                crate::config::apply_autostart(on);
            }
            _ => {}
        }
    }
}

fn set_side(left: bool) {
    crate::window::modify_config(|cfg| {
        cfg.widget_side = if left { "left" } else { "right" }.into()
    });
    crate::window::reposition_widget();
    repaint_pills();
}

fn checked(h: HWND) -> bool {
    unsafe { SendMessageW(h, BM_GETCHECK, WPARAM(0), LPARAM(0)).0 == BST_CHECKED.0 as isize }
}

fn repaint_pills() {
    unsafe {
        let Some(v) = SETTINGS.as_ref() else { return };
        let _ = InvalidateRect(v.ctl.side_left, None, false);
        let _ = InvalidateRect(v.ctl.side_right, None, false);
    }
}

/// Owner-drawn Left/Right pills, segmented accent style like the viewer's
/// range pills.
unsafe fn draw_item(dis: &mut DRAWITEMSTRUCT) {
    match dis.CtlID as i32 {
        IDC_SIDE_LEFT | IDC_SIDE_RIGHT => {
            let left = dis.CtlID as i32 == IDC_SIDE_LEFT;
            let on_left = crate::window::config_snapshot().widget_side == "left";
            let selected = left == on_left;
            let rc = dis.rcItem;
            let hdc = dis.hDC;
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
            let _ = RoundRect(
                hdc,
                rc.left,
                rc.top,
                rc.right,
                rc.bottom,
                radius * 2,
                radius * 2,
            );
            let _ = SelectObject(hdc, old_pen);
            let _ = SelectObject(hdc, old_brush);
            let _ = DeleteObject(brush);
            let _ = DeleteObject(pen);
            let _ = SetBkMode(hdc, TRANSPARENT);
            let text = crate::i18n::tr(if left { "Left" } else { "Right" });
            let mut wide: Vec<u16> = text.encode_utf16().collect();
            let mut trc = rc;
            if !wide.is_empty() {
                let _ = SelectObject(hdc, BODY_FONT);
                let _ = SetTextColor(hdc, text_color);
                DrawTextW(
                    hdc,
                    &mut wide,
                    &mut trc,
                    DT_SINGLELINE | DT_CENTER | DT_VCENTER,
                );
            }
        }
        _ => {}
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
        let Some(_) = SETTINGS.as_ref() else {
            let _ = EndPaint(hwnd, &ps);
            return;
        };
        let s = dpi_scale(hwnd);
        let (_, geoms) = geometry(s);
        for g in &geoms {
            // Section caption above the card.
            let mut cap: Vec<u16> = crate::i18n::tr(g.title).encode_utf16().collect();
            let mut crc = RECT {
                left: g.card.left,
                top: g.card.top - px(HEADER_H, s),
                right: g.card.right,
                bottom: g.card.top,
            };
            if !cap.is_empty() {
                let _ = SelectObject(hdc, CAPTION_FONT);
                let _ = SetTextColor(hdc, C_TEXT2);
                DrawTextW(
                    hdc,
                    &mut cap,
                    &mut crc,
                    DT_SINGLELINE | DT_LEFT | DT_VCENTER,
                );
            }
            draw_card(hdc, g.card, s);
            for rg in &g.rows {
                if let (Some(lrect), Row::Labeled { label, .. }) = (rg.label, rg.row) {
                    let mut txt: Vec<u16> = crate::i18n::tr(label).encode_utf16().collect();
                    let mut trc = lrect;
                    if !txt.is_empty() {
                        let _ = SelectObject(hdc, BODY_FONT);
                        let _ = SetTextColor(hdc, C_TEXT);
                        DrawTextW(
                            hdc,
                            &mut txt,
                            &mut trc,
                            DT_SINGLELINE | DT_LEFT | DT_VCENTER | DT_END_ELLIPSIS,
                        );
                    }
                }
            }
        }
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
        let _ = RoundRect(
            hdc,
            rc.left,
            rc.top,
            rc.right,
            rc.bottom,
            radius * 2,
            radius * 2,
        );
        let _ = SelectObject(hdc, old_pen);
        let _ = SelectObject(hdc, old_brush);
        let _ = DeleteObject(brush);
        let _ = DeleteObject(pen);
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
