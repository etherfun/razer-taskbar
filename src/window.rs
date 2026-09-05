//! Overlay widget floating above the taskbar (Taskbar-Lyrics style), native GDI battery UI.
//!
//! - Class `RazerTaskbarWidget`, top-level `WS_POPUP | WS_EX_LAYERED`, `HWND_TOPMOST`.
//! - `WM_PAINT` draws a Win11-style battery glyph + percentage with text shadow
//!   (readable over acrylic), black color-keyed transparent background.
//! - Right-click menu lives on the tray icon (overlay is click-through).
//! - Layout is event-driven (Taskbar-Lyrics port): `TaskbarCreated` broadcast
//!   (explorer restart) and UIA structure-change events (`uia_events.rs`) both
//!   trigger an immediately coalesced placement pass; the 1s timer stays as a
//!   fallback poll that also drives the tray refresh.

use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use windows::core::{w, PCWSTR};
use windows::Win32::Foundation::*;
use windows::Win32::Graphics::Gdi::*;
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::HiDpi::GetDpiForWindow;
use windows::Win32::UI::WindowsAndMessaging::*;

use crate::battery::{color_for, pick_device_to_display};
use crate::config::{self, Config};
use crate::hover;
use crate::settings;
use crate::taskbar;
use crate::tray;
use crate::uia_events;
use crate::viewer;
use crate::watcher::DeviceMap;

const CLASS_NAME: PCWSTR = w!("RazerTaskbarWidget");
const WINDOW_TITLE: PCWSTR = w!("RazerTaskbarWidget");
const TIMER_ID: usize = 1;
/// One-shot coalescing timer for UIA structure-change bursts (see request_layout).
const TIMER_LAYOUT: usize = 2;
/// Fast hover-poll timer driving the device-list popover (`hover.rs`).
/// The widget is click-through and gets no mouse input, so hover is polled.
const TIMER_HOVER: usize = 3;
/// Fast re-assert burst after the taskbar covered the widget: the shell
/// re-raises Shell_TrayWnd repeatedly for a short while (tray icon churn,
/// taskbar animations, settings changes), so a single SetWindowPos(HWND_TOPMOST)
/// per second can lose that race — the user sees the widget swallowed.
const TIMER_Z_BURST: usize = 4;
const Z_BURST_TICK_MS: u32 = 150;
const Z_BURST_TICKS: u32 = 12;
/// Log a "covered" event at most this often (burst repairs stay silent).
const COVERED_LOG_EVERY: Duration = Duration::from_secs(30);
const HOVER_POLL_MS: u32 = 120;
const LAYOUT_DEBOUNCE_MS: u32 = 250;
const LAYOUT_DEBOUNCE: Duration = Duration::from_millis(LAYOUT_DEBOUNCE_MS as u64);

/// Posted by `uia_events.rs` on every taskbar structure change
/// (WM_TRAY below is WM_APP+1; this is the next free WM_APP slot).
const WM_APP_LAYOUT: u32 = WM_APP + 2;

const ID_EXIT: u16 = 1001;
const ID_HISTORY_VIEW: u16 = 1010;
const ID_SETTINGS: u16 = 1011;
const ID_DEVICE_BASE: u16 = 2000;

/// Append one dynamically-translated menu item (`tr` applied; UTF-16 copy).
unsafe fn append_item(menu: HMENU, flags: MENU_ITEM_FLAGS, id: u16, text: &'static str) {
    let wide: Vec<u16> = crate::i18n::tr(text)
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect();
    let _ = AppendMenuW(menu, flags, id as usize, PCWSTR(wide.as_ptr()));
}

/// Everything drawn depends on this tuple; when it is unchanged the
/// per-tick repaint is skipped entirely (layered window content persists
/// across moves, so only content/size changes need a real repaint).
#[derive(Debug, Clone, PartialEq)]
struct PaintSig {
    label: String,
    level: u8,
    charging: bool,
    connected: bool,
    w: i32,
    h: i32,
}

struct AppState {
    devices: Arc<Mutex<DeviceMap>>,
    config: Config,
    tray: HWND,
    /// The overlay window itself (settings.rs needs it to re-create the tray
    /// icon and to re-run the placement pass after config edits).
    hwnd: HWND,
    widget_w: i32,
    widget_h: i32,
    /// Registered "TaskbarCreated" message id (0 until registered).
    taskbar_created_msg: u32,
    /// Channel signalling the UIA listener thread to rebind (taskbar HWND changed).
    uia_rebind: Option<uia_events::RebindTx>,
    /// Last screen rect we moved the overlay to (dedup for move + invalidate).
    last_layout: Option<(i32, i32, i32, i32)>,
    /// Last placement log line (dedup — placement runs every 1s / per event).
    last_log: Option<String>,
    /// Last drawn content signature (dedup for WM_PAINT).
    painted_sig: Option<PaintSig>,
    /// Coalescing state for UIA structure-change driven layouts.
    last_layout_pass: Instant,
    layout_pending: bool,
    /// Remaining fast re-assert ticks after a z-order loss (TIMER_Z_BURST).
    z_burst_left: u32,
    /// Dedup for the "covered" log line.
    last_covered_log: Option<Instant>,
}

static mut STATE: Option<AppState> = None;

fn state_mut() -> &'static mut AppState {
    #[allow(static_mut_refs)]
    unsafe {
        STATE.as_mut().expect("window state")
    }
}

fn state() -> Option<&'static AppState> {
    #[allow(static_mut_refs)]
    unsafe {
        STATE.as_ref()
    }
}

// — Settings-window surface (`settings.rs` runs on this same UI thread) —

/// Apply a config mutation to the widget's copy and persist it. All settings
/// edits flow through here so `STATE.config` stays the single authoritative
/// value; consumers without a UI-thread presence (watcher, history) read the
/// rewritten settings.json on their own cadence.
pub fn modify_config(f: impl FnOnce(&mut Config)) {
    let st = state_mut();
    f(&mut st.config);
    config::save(&st.config);
}

/// Snapshot for initializing/refreshing the settings window's controls.
pub fn config_snapshot() -> Config {
    state().map(|s| s.config.clone()).unwrap_or_default()
}

pub fn devices_arc() -> Option<Arc<Mutex<DeviceMap>>> {
    state().map(|s| s.devices.clone())
}

pub fn widget_hwnd() -> HWND {
    state()
        .map(|s| s.hwnd)
        .unwrap_or(HWND(std::ptr::null_mut()))
}

/// Re-run the placement pass and repaint the widget (after edits that change
/// position or width: side, avoid-overlap, estimated-time toggle).
pub fn reposition_widget() {
    let Some(st) = state() else { return };
    let hwnd = st.hwnd;
    place_widget(hwnd, false);
    unsafe {
        let _ = InvalidateRect(hwnd, None, true);
    }
}

pub fn invalidate_widget() {
    if let Some(st) = state() {
        unsafe {
            let _ = InvalidateRect(st.hwnd, None, true);
        }
    }
}

/// Switch the displayed device ("" = auto). Shared by the tray menu and the
/// settings window: stamps `is_selected` so `pick_device_to_display` follows
/// immediately (the watcher re-reads the config each cycle regardless).
pub fn set_shown_device(handle: &str) {
    {
        let st = state_mut();
        st.config.shown_device_handle = handle.to_string();
        config::save(&st.config);
        let shown = st.config.shown_device_handle.clone();
        let mut devices = st.devices.lock().unwrap();
        for d in devices.values_mut() {
            d.is_selected = shown.is_empty() || d.handle == shown;
        }
    }
    invalidate_widget();
}

/// True if a widget window from a previous instance already exists.
/// Overlay mode uses a top-level WS_POPUP, which FindWindow CAN see.
pub fn find_existing_instance() -> bool {
    unsafe {
        FindWindowW(CLASS_NAME, WINDOW_TITLE)
            .map(|h| !h.0.is_null())
            .unwrap_or(false)
    }
}

pub fn run_message_loop(devices: Arc<Mutex<DeviceMap>>, cfg: Config) {
    unsafe {
        let instance = GetModuleHandleW(None).unwrap_or_default();

        let wc = WNDCLASSW {
            style: CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc: Some(wnd_proc),
            hInstance: instance.into(),
            lpszClassName: CLASS_NAME,
            hCursor: LoadCursorW(None, IDC_ARROW).unwrap_or_default(),
            hbrBackground: HBRUSH(std::ptr::null_mut()),
            ..Default::default()
        };
        RegisterClassW(&wc);

        // Taskbar-Lyrics style overlay: top-level WS_POPUP, NOT a WS_CHILD.
        // A child of Shell_TrayWnd is composited INTO the taskbar, so
        // taskbar-wide effects (TranslucentTB acrylic via
        // SetWindowCompositionAttribute) paint over it. A top-level layered
        // window at HWND_TOPMOST composites above the taskbar instead.
        // Layout is event-driven: TaskbarCreated + UIA structure changes
        // (see below), with the 1s timer as fallback poll.
        let tray = match taskbar::find_shell_tray() {
            Some(t) => t,
            None => {
                eprintln!("razer-taskbar: Shell_TrayWnd not found");
                return;
            }
        };

        // 144x48: full taskbar height, wide enough for icon + "100%" text.
        let (init_w, init_h) = (144, 48);
        let hwnd = CreateWindowExW(
            WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE,
            CLASS_NAME,
            WINDOW_TITLE,
            WS_POPUP | WS_VISIBLE | WS_CLIPSIBLINGS,
            0,
            0,
            init_w,
            init_h,
            None,
            None,
            instance,
            None,
        )
        .unwrap_or(HWND(std::ptr::null_mut()));
        if hwnd.0.is_null() {
            eprintln!(
                "razer-taskbar: CreateWindowExW failed: {:?}",
                windows::Win32::Foundation::GetLastError()
            );
            return;
        }

        STATE = Some(AppState {
            devices,
            config: cfg,
            tray,
            hwnd,
            widget_w: init_w,
            widget_h: init_h,
            taskbar_created_msg: 0,
            uia_rebind: None,
            last_layout: None,
            last_log: None,
            painted_sig: None,
            last_layout_pass: Instant::now(),
            layout_pending: false,
            z_burst_left: 0,
            last_covered_log: None,
        });
        // Color-key transparency: BLACK key is cut out (LWA_COLORKEY).
        // Black (not magenta): glyph/text antialiased edges blend toward
        // black, leaving dark-gray fringe that melts into the dark taskbar.
        // Magenta key left pink-purple fringe (white mixed with magenta).
        // NOTE: single call with COLORKEY only — a second LWA_ALPHA call
        // would replace the key mode instead of combining with it.
        let _ = SetLayeredWindowAttributes(hwnd, COLORREF(0x00000000), 0, LWA_COLORKEY);

        // First call anchors directly (first=true bypasses hold AND the
        // jump cap): the window still sits at its 0,0 creation rect, which
        // is not a position worth defending.
        place_widget(hwnd, true);
        let show_tray = state_mut().config.show_tray_icon;
        if show_tray {
            tray::set_devices(state_mut().devices.clone());
            tray::ensure_created(hwnd);
        }
        hover::set_devices(state_mut().devices.clone());
        let _ = SetTimer(hwnd, TIMER_ID, 1000, None);
        let _ = SetTimer(hwnd, TIMER_HOVER, HOVER_POLL_MS, None);

        // Event-driven re-layout (Taskbar-Lyrics port):
        // - "TaskbarCreated" broadcast: explorer restarted → re-bind taskbar,
        //   tray icon and UIA listener, re-anchor.
        // - UIA structure changes on the taskbar: coalesced placement pass.
        // The 1s timer above stays as the fallback poll.
        let taskbar_created_msg = RegisterWindowMessageW(w!("TaskbarCreated"));
        let uia_rebind = uia_events::spawn(hwnd, WM_APP_LAYOUT);
        let st = state_mut();
        st.taskbar_created_msg = taskbar_created_msg;
        st.uia_rebind = Some(uia_rebind);

        let mut msg = MSG::default();
        while GetMessageW(&mut msg, None, 0, 0).as_bool() {
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
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

fn widget_size(hwnd: HWND) -> (i32, i32) {
    let s = dpi_scale(hwnd);
    // Widened while the predicted usage time shows next to the percentage.
    let base_w = if state()
        .map(|st| st.config.show_estimated_time)
        .unwrap_or(false)
    {
        216.0
    } else {
        144.0
    };
    // Taskbar is 48px tall at 96 DPI: fill it edge-to-edge like TrafficMonitor
    // instead of the old 32px-high floating look.
    (
        ((base_w * s).round() as i32).max(96),
        ((48.0 * s).round() as i32).max(32),
    )
}

/// Recompute and apply the placement. Runs on the 1s fallback poll, after
/// every coalesced UIA structure event, and on first anchor — so both the
/// move/invalidate and the stderr log are deduped against the previous pass.
fn place_widget(hwnd: HWND, first: bool) {
    let st = state_mut();
    let (w, h) = widget_size(hwnd);
    st.widget_w = w;
    st.widget_h = h;
    let side = st.config.widget_side.clone();
    let cfg = st.config.clone();
    let tray = st.tray;

    let Some(pl) = taskbar::compute_placement(
        tray,
        hwnd,
        w,
        h,
        &side,
        cfg.window_offset_left,
        cfg.window_offset_top,
        cfg.taskbar_left_space_win11,
        cfg.taskbar_right_space_win11,
        cfg.avoid_overlap_with_widgets,
        cfg.avoid_overlap,
        first,
    ) else {
        return;
    };

    // Convert to screen coords relative to the placement's own parent: the
    // Win11 branch anchors on Shell_TrayWnd, the Classic branch on the
    // ReBarWindow32 band — `pl.parent` carries which one. (Using the tray
    // rect for both mis-places Win10 by the Start-button width.)
    let (parent_x, parent_y) = taskbar::window_rect(pl.parent)
        .map(|r| (r.left, r.top))
        .unwrap_or((0, 0));
    let sx = parent_x + pl.x;
    let sy = parent_y + pl.y;

    // One log line per distinct state; unchanged states stay silent.
    let describe = |o: &taskbar::Occupant| {
        if o.exe.is_empty() {
            o.class.clone()
        } else {
            format!("{} ({})", o.exe, o.class)
        }
    };
    let mut line = format!(
        "overlay kind={:?} pos=({sx},{sy}) size={w}x{h} held={} capped={}",
        pl.kind, pl.held, pl.capped
    );
    if pl.capped && !pl.blockers.is_empty() {
        let names: Vec<String> = pl.blockers.iter().map(describe).collect();
        line.push_str(&format!(
            " | staying despite overlap with {} (avoid jump capped at {}px)",
            names.join(", "),
            taskbar::MAX_AVOID_JUMP_PX,
        ));
    } else if !pl.blockers.is_empty() {
        let names: Vec<String> = pl.blockers.iter().map(describe).collect();
        line.push_str(&format!(" | yielding to occupant(s): {}", names.join(", ")));
    } else if !pl.occupants.is_empty() {
        let names: Vec<String> = pl.occupants.iter().map(describe).collect();
        line.push_str(&format!(
            " | coexisting with occupant(s) (no overlap): {}",
            names.join(", ")
        ));
    }
    if st.last_log.as_deref() != Some(line.as_str()) {
        eprintln!("razer-taskbar: {line}");
        st.last_log = Some(line);
    }

    // Apply. move_overlay also re-asserts HWND_TOPMOST every pass (cheap
    // z-order re-claim against other topmost tools). A pure move needs no
    // repaint — layered window content survives — so invalidate only when
    // the rect actually changed.
    let changed = st.last_layout != Some((sx, sy, w, h));
    taskbar::move_overlay(hwnd, sx, sy, w, h);
    if changed {
        st.last_layout = Some((sx, sy, w, h));
        unsafe {
            let _ = InvalidateRect(hwnd, None, true);
        }
    }
}

unsafe extern "system" fn wnd_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    match msg {
        WM_TIMER => {
            match wparam.0 {
                TIMER_ID => {
                    // Detect "the taskbar raised itself above us" BEFORE the
                    // re-asserting placement pass, and arm a fast repair
                    // burst so we win the re-raise race (one pass per second
                    // alone is too slow and read as the widget vanishing).
                    if z_covered(hwnd) {
                        arm_z_burst(hwnd);
                    }
                    place_widget(hwnd, false);
                    tray::refresh();
                    // A layout request coalesced right before the poll tick
                    // should not wait for its own timer — flush it now.
                    flush_pending_layout(hwnd);
                }
                TIMER_LAYOUT => {
                    let _ = KillTimer(hwnd, TIMER_LAYOUT);
                    flush_pending_layout(hwnd);
                }
                TIMER_HOVER => {
                    // Piggyback the z-order check on this 120ms tick: the
                    // walk is a few syscalls, and coverage recovery drops
                    // from ≤1s to ≤120ms (Taskbar-Lyrics parity — they
                    // re-assert instantly on every UIA structure event).
                    if z_covered(hwnd) {
                        arm_z_burst(hwnd);
                    }
                    let enabled = state_mut().config.hover_devices;
                    hover::track(hwnd, enabled);
                }
                TIMER_Z_BURST => {
                    let left = {
                        let st = state_mut();
                        st.z_burst_left = st.z_burst_left.saturating_sub(1);
                        st.z_burst_left
                    };
                    if left == 0 {
                        let _ = KillTimer(hwnd, TIMER_Z_BURST);
                    } else {
                        // place_widget ends in move_overlay → re-asserts
                        // HWND_TOPMOST above the taskbar.
                        place_widget(hwnd, false);
                    }
                }
                _ => {}
            }
            LRESULT(0)
        }
        _ if msg == WM_APP_LAYOUT => {
            // UIA structure change on the taskbar (coalesced).
            request_layout(hwnd);
            LRESULT(0)
        }
        _ if msg == taskbar_created_msg() => {
            handle_taskbar_created(hwnd);
            LRESULT(0)
        }
        WM_PAINT => {
            paint(hwnd);
            LRESULT(0)
        }
        // Taskbar-Lyrics answers WM_NCHITTEST with HTTRANSPARENT so the
        // overlay never steals taskbar clicks; the tray icon carries the
        // menu instead. (A full HTTRANSPARENT window also skips RBUTTON
        // messages, so the widget menu is intentionally tray-only.)
        WM_NCHITTEST => LRESULT(HTTRANSPARENT as isize),
        WM_DESTROY => {
            hover::destroy();
            tray::destroy();
            viewer::destroy();
            settings::destroy();
            crate::history::close();
            PostQuitMessage(0);
            LRESULT(0)
        }
        WM_COMMAND => {
            handle_command(hwnd, (wparam.0 & 0xFFFF) as u16);
            LRESULT(0)
        }
        _ if msg == tray::tray_callback_msg() => {
            // Tray callbacks arrive as (msg=WM_TRAY, wparam=uid, lparam=event).
            // Right-click (up or down, some shells only send down) opens the
            // same menu as the widget.
            let event = (lparam.0 & 0xFFFF) as u32;
            if event == WM_RBUTTONUP || event == WM_RBUTTONDOWN {
                show_menu(hwnd);
            }
            LRESULT(0)
        }
        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}

/// `true` while Shell_TrayWnd sits ABOVE our overlay in the topmost band —
/// the taskbar visually covers the widget (both are TOPMOST; the later
/// raise wins).
fn z_covered(hwnd: HWND) -> bool {
    let tray = state().map(|st| st.tray);
    match tray {
        Some(tray) => taskbar::tray_above(hwnd, tray),
        None => false,
    }
}

/// Arm the fast re-assert burst (see TIMER_Z_BURST). Only start it when it
/// is not already running — a repeated SetTimer would RESET the countdown,
/// and the 120ms hover check firing while covered would keep postponing the
/// 150ms burst forever (burst starvation, seen in testing).
fn arm_z_burst(hwnd: HWND) {
    let st = state_mut();
    if st
        .last_covered_log
        .map(|t| t.elapsed() >= COVERED_LOG_EVERY)
        .unwrap_or(true)
    {
        eprintln!("razer-taskbar: taskbar covers the widget — re-asserting topmost");
        st.last_covered_log = Some(Instant::now());
    }
    if st.z_burst_left == 0 {
        st.z_burst_left = Z_BURST_TICKS;
        unsafe {
            let _ = SetTimer(hwnd, TIMER_Z_BURST, Z_BURST_TICK_MS, None);
        }
    }
}

/// Coalesce UIA structure-change bursts (Taskbar-Lyrics `layoutPending`
/// pattern): at most one placement pass per `LAYOUT_DEBOUNCE`, with a
/// trailing pass on a one-shot timer so the last request always lands.
fn request_layout(hwnd: HWND) {
    let due = {
        let st = state_mut();
        if st.last_layout_pass.elapsed() >= LAYOUT_DEBOUNCE {
            st.last_layout_pass = Instant::now();
            true
        } else {
            st.layout_pending = true;
            false
        }
    };
    if due {
        place_widget(hwnd, false);
    } else {
        unsafe {
            let _ = SetTimer(hwnd, TIMER_LAYOUT, LAYOUT_DEBOUNCE_MS, None);
        }
    }
}

fn flush_pending_layout(hwnd: HWND) {
    let run = {
        let st = state_mut();
        let run = st.layout_pending;
        st.layout_pending = false;
        if run {
            st.last_layout_pass = Instant::now();
        }
        run
    };
    if run {
        place_widget(hwnd, false);
    }
}

/// The registered "TaskbarCreated" id; 0 (never matches) before init.
fn taskbar_created_msg() -> u32 {
    state().map(|s| s.taskbar_created_msg).unwrap_or(0)
}

/// Explorer (re)started: the cached taskbar HWND, hold state, UIA caches and
/// the notification-area icon are all stale — rebind everything, re-anchor.
fn handle_taskbar_created(hwnd: HWND) {
    eprintln!("razer-taskbar: TaskbarCreated — re-binding to taskbar");
    let st = state_mut();
    if let Some(tray) = taskbar::find_shell_tray() {
        st.tray = tray;
    }
    taskbar::reset_hold_state();
    taskbar::invalidate_widgets_cache();
    st.last_layout = None;
    if st.config.show_tray_icon {
        // A fresh taskbar wiped all tray icons: ensure_created re-registers
        // (same hWnd/uID → replace, never duplicate).
        tray::ensure_created(hwnd);
    }
    if let Some(tx) = st.uia_rebind.clone() {
        let _ = tx.send(());
    }
    // first=true bypasses hold + jump cap: the old position is meaningless.
    place_widget(hwnd, true);
}

/// Win11-style battery UI: glyph + percentage, transparent background.
///
/// Design (v2): no chip background — magenta color-key keeps the window
/// transparent so TTB acrylic / taskbar texture shows through. Only the
/// glyph outline, proportional fill, bolt and text are drawn, all with a
/// soft drop shadow for readability over busy backdrops. Layout is centered
/// as a group and measured from the real text width (no fixed slots).
fn paint(hwnd: HWND) {
    let device = {
        let st = state_mut();
        let devices = st.devices.lock().unwrap();
        pick_device_to_display(&devices)
    };
    let st = state_mut();
    let scale = dpi_scale(hwnd);
    let w = st.widget_w;
    let h = st.widget_h;
    let (level, charging) = device
        .as_ref()
        .map(|d| (d.battery_percentage, d.is_charging))
        .unwrap_or((0, false));
    let connected = device.as_ref().map(|d| d.is_connected).unwrap_or(false);
    // Percentage text (+ predicted usage time when enabled). No device yet:
    // dim "--".
    let label = device
        .as_ref()
        .filter(|_| connected)
        .map(|d| {
            let mut l = format!("{}%", d.battery_percentage);
            if st.config.show_estimated_time {
                if let Some(e) = crate::history::estimate_for(&d.handle) {
                    l.push_str(&format!(
                        " · {}",
                        crate::history::format_estimate_compact(e)
                    ));
                }
            }
            l
        })
        .unwrap_or_else(|| "--".into());
    let sig = PaintSig {
        label: label.clone(),
        level,
        charging,
        connected,
        w,
        h,
    };
    if st.painted_sig.as_ref() == Some(&sig) {
        // Nothing visually different; still validate the update region so
        // WM_PAINT does not loop. (Runs at most once per second, and only
        // after something actually invalidated the window.)
        unsafe {
            let mut ps = PAINTSTRUCT::default();
            let hdc = BeginPaint(hwnd, &mut ps);
            if !hdc.is_invalid() {
                let _ = EndPaint(hwnd, &ps);
            }
        }
        return;
    }
    unsafe {
        {
            use std::sync::atomic::{AtomicBool, Ordering};
            static LOGGED: AtomicBool = AtomicBool::new(false);
            if !LOGGED.swap(true, Ordering::Relaxed) {
                let n = state_mut().devices.lock().unwrap().len();
                eprintln!("razer-taskbar: first paint, devices={n}");
            }
        }
        let mut ps = PAINTSTRUCT::default();
        let screen_hdc = BeginPaint(hwnd, &mut ps);
        if screen_hdc.is_invalid() {
            return;
        }
        let hdc = screen_hdc;
        // Transparent base: black color-key is cut out (LWA_COLORKEY),
        // so only drawn pixels are visible over acrylic/taskbar.
        let key_brush = CreateSolidBrush(COLORREF(0x00000000));
        let key_rect = RECT {
            left: 0,
            top: 0,
            right: w,
            bottom: h,
        };
        let _ = FillRect(hdc, &key_rect, key_brush);
        let _ = DeleteObject(key_brush);
        let _ = SetBkMode(hdc, TRANSPARENT);

        // Palette: white glyph/text, dark-gray shadow, level fill colors.
        // Shadow must NOT be pure black — it would match the color key and
        // be cut out. Grayscale (not ClearType) antialiasing avoids colored
        // subpixel fringe around glyphs.
        let fg_color = COLORREF(0x00FFFFFF);
        let dim = COLORREF(0x00B0B0B0);
        let shadow = COLORREF(0x00202020);

        // Native Win11 glyphs: Segoe Fluent Icons battery (E850-E85A) +
        // Segoe UI Variable Text for the percentage. Both ship with Win11.
        let icon_h = (22.0 * scale).round() as i32;
        let hicon_font = CreateFontW(
            icon_h,
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
        let hicon_font = if hicon_font.is_invalid() {
            CreateFontW(
                icon_h,
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
                w!("Segoe MDL2 Assets"),
            )
        } else {
            hicon_font
        };
        let font_h = (16.0 * scale).round() as i32;
        let hfont = CreateFontW(
            font_h,
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
        let hfont = if hfont.is_invalid() {
            CreateFontW(
                font_h,
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
                w!("Segoe UI"),
            )
        } else {
            hfont
        };
        let old_font = SelectObject(hdc, hfont);

        let (fr, fg, fb) = if connected {
            color_for(level)
        } else {
            (0x80, 0x80, 0x80)
        };
        let text_color = if connected { fg_color } else { dim };
        let mut wide: Vec<u16> = label.encode_utf16().collect();
        // Measure text first so the glyph+text group can be centered.
        let mut measure = RECT {
            left: 0,
            top: 0,
            right: 0,
            bottom: 0,
        };
        DrawTextW(
            hdc,
            &mut wide,
            &mut measure,
            DT_SINGLELINE | DT_CALCRECT | DT_LEFT,
        );
        let text_w = (measure.right - measure.left).max(1);
        // Measure the native icon glyph the same way.
        let icon_ch = [crate::battery::fluent_battery_glyph(level, charging) as u16];
        let mut icon_measure = RECT {
            left: 0,
            top: 0,
            right: 0,
            bottom: 0,
        };
        let _ = SelectObject(hdc, hicon_font);
        DrawTextW(
            hdc,
            &mut icon_ch.clone(),
            &mut icon_measure,
            DT_SINGLELINE | DT_CALCRECT | DT_LEFT,
        );
        let icon_w = (icon_measure.right - icon_measure.left).max(1);
        let _ = SelectObject(hdc, hfont);
        // Geometry: [device-type icon] [battery glyph] [percentage], the
        // group centered. The type icon is omitted while no device is
        // shown ("--") — nothing meaningful to classify then.
        let gap = (6.0 * scale).round() as i32;
        let kind = device.as_ref().map(|d| d.kind);
        let kind_h = (16.0 * scale).round() as i32;
        let kind_w = kind
            .map(|k| crate::icons::width_for(kind_h, k))
            .unwrap_or(0);
        let kind_gap = if kind.is_some() { gap } else { 0 };
        let group_w = kind_w + kind_gap + icon_w + gap + text_w;
        let group_x = (w - group_w) / 2;
        let icon_x = group_x + kind_w + kind_gap;
        let text_x = icon_x + icon_w + gap;
        if let Some(k) = kind {
            let kc = if connected {
                (0xFF, 0xFF, 0xFF)
            } else {
                (0x80, 0x80, 0x80)
            };
            crate::icons::draw(hdc, group_x, (h - kind_h) / 2, kind_h, k, kc);
        }
        // Native icon glyph (level-tinted) with drop shadow.
        let icon_rgb = if connected {
            (fr, fg, fb)
        } else {
            (0x80, 0x80, 0x80)
        };
        let _ = SelectObject(hdc, hicon_font);
        let mut icon_shadow_rect = RECT {
            left: icon_x + 1,
            top: 1,
            right: w,
            bottom: h,
        };
        let _ = SetTextColor(hdc, shadow);
        let _ = DrawTextW(
            hdc,
            &mut icon_ch.clone(),
            &mut icon_shadow_rect,
            DT_SINGLELINE | DT_VCENTER | DT_LEFT,
        );
        let mut icon_rect = RECT {
            left: icon_x,
            top: 0,
            right: w,
            bottom: h,
        };
        let _ = SetTextColor(
            hdc,
            COLORREF(icon_rgb.0 as u32 | ((icon_rgb.1 as u32) << 8) | ((icon_rgb.2 as u32) << 16)),
        );
        DrawTextW(
            hdc,
            &mut icon_ch.clone(),
            &mut icon_rect,
            DT_SINGLELINE | DT_VCENTER | DT_LEFT,
        );
        let _ = SelectObject(hdc, hfont);
        // Text with drop shadow (offset 1px, drawn first underneath).
        let mut shadow_rect = RECT {
            left: text_x + 1,
            top: 1,
            right: w,
            bottom: h,
        };
        let _ = SetTextColor(hdc, shadow);
        let _ = DrawTextW(
            hdc,
            &mut wide.clone(),
            &mut shadow_rect,
            DT_SINGLELINE | DT_VCENTER | DT_LEFT,
        );
        let mut text_rect = RECT {
            left: text_x,
            top: 0,
            right: w,
            bottom: h,
        };
        let _ = SetTextColor(hdc, text_color);
        DrawTextW(
            hdc,
            &mut wide,
            &mut text_rect,
            DT_SINGLELINE | DT_VCENTER | DT_LEFT,
        );
        let _ = SelectObject(hdc, old_font);
        let _ = DeleteObject(hfont);
        let _ = DeleteObject(hicon_font);
        st.painted_sig = Some(sig);
        let _ = EndPaint(hwnd, &ps);
    }
}

/// Draw the battery glyph (legacy GDI path, kept for reference).
/// v2 design: 1px outline, inset fill with rounded ends, nub cap, and a
/// white bolt with dark edge when charging (readable over any fill level).
#[allow(dead_code, clippy::too_many_arguments)]
fn draw_battery_glyph(
    hdc: HDC,
    body_x: i32,
    body_y: i32,
    body_w: i32,
    body_h: i32,
    cap_w: i32,
    scale: f32,
    level: u8,
    charging: bool,
    connected: bool,
    fill_rgb: (u8, u8, u8),
    fg: COLORREF,
    shadow: COLORREF,
) {
    unsafe {
        let cap_h = ((body_h as f32 * 0.45).round() as i32).max(5);
        let cap_y = body_y + (body_h - cap_h) / 2;
        // Shadow pass (offset +1,+1, black): outline + cap silhouettes.
        let sh_pen = CreatePen(PS_SOLID, 1, shadow);
        let old_pen = SelectObject(hdc, sh_pen);
        let old_brush = SelectObject(hdc, GetStockObject(NULL_BRUSH));
        let _ = RoundRect(
            hdc,
            body_x + 1,
            body_y + 1,
            body_x + body_w + 1,
            body_y + body_h + 1,
            5,
            5,
        );
        let sh_cap = CreateSolidBrush(shadow);
        let _ = SelectObject(hdc, sh_cap);
        let _ = RoundRect(
            hdc,
            body_x + body_w + 2,
            cap_y + 1,
            body_x + body_w + 2 + cap_w,
            cap_y + 1 + cap_h,
            2,
            2,
        );
        let _ = DeleteObject(sh_cap);
        let _ = SelectObject(hdc, old_pen);
        let _ = SelectObject(hdc, old_brush);
        let _ = DeleteObject(sh_pen);
        // Foreground outline + cap.
        let outline = CreatePen(PS_SOLID, 1, fg);
        let old_pen = SelectObject(hdc, outline);
        let old_brush = SelectObject(hdc, GetStockObject(NULL_BRUSH));
        let _ = RoundRect(hdc, body_x, body_y, body_x + body_w, body_y + body_h, 5, 5);
        let cap_brush = CreateSolidBrush(fg);
        let _ = SelectObject(hdc, cap_brush);
        let _ = RoundRect(
            hdc,
            body_x + body_w + 1,
            cap_y,
            body_x + body_w + 1 + cap_w,
            cap_y + cap_h,
            2,
            2,
        );
        let _ = DeleteObject(cap_brush);
        // Inset fill with rounded ends: inset 2px, radius follows body.
        let (fr, fg_, fb) = if connected {
            fill_rgb
        } else {
            (0x80, 0x80, 0x80)
        };
        let inner_w = (body_w - 4).max(0);
        let fill_w = inner_w * level.clamp(0, 100) as i32 / 100;
        if fill_w > 0 {
            let fill = CreateSolidBrush(COLORREF(
                fr as u32 | ((fg_ as u32) << 8) | ((fb as u32) << 16),
            ));
            let old_fill = SelectObject(hdc, fill);
            let old_pen2 = SelectObject(hdc, GetStockObject(NULL_PEN));
            let fx = body_x + 2;
            let fy = body_y + 2;
            let fh = (body_h - 4).max(1);
            let _ = RoundRect(hdc, fx, fy, fx + fill_w.max(2), fy + fh, 3, 3);
            let _ = SelectObject(hdc, old_pen2);
            let _ = SelectObject(hdc, old_fill);
            let _ = DeleteObject(fill);
        }
        // Charging bolt: white core + dark edge, centered on the glyph.
        if charging {
            let cx = body_x + body_w / 2;
            let cy = body_y + body_h / 2;
            let s = scale.max(1.0);
            let pts = [
                POINT {
                    x: (cx as f32 + 1.6 * s) as i32,
                    y: (cy as f32 - 5.5 * s) as i32,
                },
                POINT {
                    x: (cx as f32 - 2.2 * s) as i32,
                    y: (cy as f32 + 1.2 * s) as i32,
                },
                POINT {
                    x: (cx as f32 - 0.2 * s) as i32,
                    y: (cy as f32 + 1.2 * s) as i32,
                },
                POINT {
                    x: (cx as f32 - 1.6 * s) as i32,
                    y: (cy as f32 + 5.5 * s) as i32,
                },
                POINT {
                    x: (cx as f32 + 2.2 * s) as i32,
                    y: (cy as f32 - 1.2 * s) as i32,
                },
                POINT {
                    x: (cx as f32 + 0.2 * s) as i32,
                    y: (cy as f32 - 1.2 * s) as i32,
                },
            ];
            // Dark edge: draw the polygon expanded by 1px first.
            let edge = CreateSolidBrush(COLORREF(0x001A1A1A));
            let _ = SelectObject(hdc, edge);
            let grown: Vec<POINT> = pts.iter().map(|p| POINT { x: p.x, y: p.y }).collect();
            let _ = Polygon(hdc, &pts);
            let _ = DeleteObject(edge);
            // White core, slightly smaller.
            let core = CreateSolidBrush(COLORREF(0x00FFFFFF));
            let _ = SelectObject(hdc, core);
            let inset: Vec<POINT> = pts
                .iter()
                .map(|p| POINT {
                    x: cx + ((p.x - cx) * 3 / 4),
                    y: cy + ((p.y - cy) * 3 / 4),
                })
                .collect();
            let _ = Polygon(hdc, &inset);
            let _ = DeleteObject(core);
            let _ = grown;
        }
        let _ = SelectObject(hdc, old_pen);
        let _ = SelectObject(hdc, old_brush);
        let _ = DeleteObject(outline);
    }
}

fn show_menu(hwnd: HWND) {
    unsafe {
        let st = state_mut();
        let menu = CreatePopupMenu().unwrap_or_default();
        if menu.is_invalid() {
            return;
        }

        // Device radio items: the only thing worth keeping in the menu
        // (quick switching); every other option moved into the Settings
        // window (`settings.rs`).
        let devices = st.devices.lock().unwrap();
        let mut connected: Vec<_> = devices.values().filter(|d| d.is_connected).collect();
        connected.sort_by(|a, b| a.name.cmp(&b.name));
        // "All devices" entry.
        let all_checked = if st.config.shown_device_handle.is_empty() {
            MF_CHECKED
        } else {
            MF_UNCHECKED
        };
        append_item(menu, MF_STRING | all_checked, ID_DEVICE_BASE, "All devices");
        for (i, d) in connected.iter().enumerate() {
            let checked = if st.config.shown_device_handle == d.handle {
                MF_CHECKED
            } else {
                MF_UNCHECKED
            };
            let label = format!(
                "{} — {}%{}",
                d.name,
                d.battery_percentage,
                if d.is_charging { " ⚡" } else { "" }
            );
            let wide: Vec<u16> = label.encode_utf16().chain(std::iter::once(0)).collect();
            let _ = AppendMenuW(
                menu,
                MF_STRING | checked,
                (ID_DEVICE_BASE + 1 + i as u16) as usize,
                PCWSTR(wide.as_ptr()),
            );
        }
        drop(devices);
        let _ = AppendMenuW(menu, MF_SEPARATOR, 0, None);

        append_item(menu, MF_STRING, ID_SETTINGS, "Settings…");
        append_item(menu, MF_STRING, ID_HISTORY_VIEW, "Battery history…");
        let _ = AppendMenuW(menu, MF_SEPARATOR, 0, None);
        append_item(menu, MF_STRING, ID_EXIT, "Exit");

        let mut cursor = POINT::default();
        let _ = GetCursorPos(&mut cursor);
        let _ = SetForegroundWindow(hwnd);
        let _ = TrackPopupMenu(
            menu,
            TPM_LEFTALIGN | TPM_RIGHTBUTTON,
            cursor.x,
            cursor.y,
            0,
            hwnd,
            None,
        );
        let _ = DestroyMenu(menu);
    }
}

fn handle_command(hwnd: HWND, id: u16) {
    let st = state_mut();
    match id {
        ID_EXIT => unsafe {
            let _ = DestroyWindow(hwnd);
        },
        ID_SETTINGS => {
            settings::open();
        }
        ID_HISTORY_VIEW => {
            viewer::open();
        }
        id if id >= ID_DEVICE_BASE => {
            let idx = (id - ID_DEVICE_BASE) as usize;
            let handle = if idx == 0 {
                String::new()
            } else {
                let devices = st.devices.lock().unwrap();
                let mut connected: Vec<_> = devices.values().filter(|d| d.is_connected).collect();
                connected.sort_by(|a, b| a.name.cmp(&b.name));
                connected
                    .get(idx - 1)
                    .map(|d| d.handle.clone())
                    .unwrap_or_default()
            };
            set_shown_device(&handle);
        }
        _ => {}
    }
}
