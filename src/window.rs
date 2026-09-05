//! Overlay widget floating above the taskbar (Taskbar-Lyrics style), native GDI battery UI.
//!
//! - Class `RazerTaskbarWidget`, top-level `WS_POPUP | WS_EX_LAYERED`, `HWND_TOPMOST`.
//! - `WM_PAINT` draws a Win11-style two-row widget — battery glyph +
//!   percentage on top, predicted time below (see `history.rs`) — with text
//!   shadow (readable over acrylic), black color-keyed transparent background.
//! - Right-click menu lives on the tray icon (overlay is click-through).
//! - Layout is event-driven (Taskbar-Lyrics port): `TaskbarCreated` broadcast
//!   (explorer restart) and UIA structure-change events (`uia_events.rs`) both
//!   trigger an immediately coalesced placement pass; the 1s timer stays as a
//!   fallback poll that also drives the tray refresh.

use std::collections::HashMap;
use std::sync::{Arc, Mutex, OnceLock};
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
    /// Row 1: the percentage ("100%") or "--" with no device.
    top: String,
    /// Row 2: compact predicted time ("~3h25m" / "+1h10m"); empty = no row.
    bottom: String,
    level: u8,
    charging: bool,
    saver: bool,
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
    /// Experimental embed mode: the widget is a WS_CHILD of the taskbar band
    /// instead of a topmost overlay (see `taskbar::set_taskbar_child`).
    embedded: bool,
    /// Sticky: a SetParent rejection disables embed mode until restart (the
    /// blocker — usually security software — will not vanish mid-session).
    embed_failed: bool,
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
    place_widget(hwnd);
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
/// Embed mode parents the widget into the taskbar band, where only an
/// EnumChildWindows walk can find it.
pub fn find_existing_instance() -> bool {
    unsafe {
        if FindWindowW(CLASS_NAME, WINDOW_TITLE)
            .map(|h| !h.0.is_null())
            .unwrap_or(false)
        {
            return true;
        }
        let Some(tray) = taskbar::find_shell_tray() else {
            return false;
        };
        struct Ctx {
            found: bool,
            want: Vec<u16>,
        }
        unsafe extern "system" fn proc_cb(hwnd: HWND, lparam: LPARAM) -> BOOL {
            let ctx = unsafe { &mut *(lparam.0 as *mut Ctx) };
            let mut name = [0u16; 64];
            let n = GetClassNameW(hwnd, &mut name) as usize;
            if name[..n] == ctx.want[..] {
                ctx.found = true;
                return BOOL(0);
            }
            BOOL(1)
        }
        let mut ctx = Ctx {
            found: false,
            want: CLASS_NAME.to_string().unwrap().encode_utf16().collect(),
        };
        let _ = EnumChildWindows(tray, Some(proc_cb), LPARAM(&mut ctx as *mut Ctx as isize));
        ctx.found
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
            embedded: false,
            embed_failed: false,
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
        place_widget(hwnd);
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
    // 144px at 96 DPI fits the widest row (type icon + glyph + "100%"). The
    // predicted time stacks as a second row instead of widening the widget.
    // Height is compressed to 40px at 96 DPI (taskbar is 48): the two-row
    // stack needs less than the full band, and placement centers it.
    (
        ((144.0 * s).round() as i32).max(96),
        ((40.0 * s).round() as i32).max(32),
    )
}

/// Recompute and apply the placement. Runs on the 1s fallback poll, after
/// every coalesced UIA structure event, and on first anchor — so both the
/// move/invalidate and the stderr log are deduped against the previous pass.
fn place_widget(hwnd: HWND) {
    let st = state_mut();
    let (w, h) = widget_size(hwnd);
    st.widget_w = w;
    st.widget_h = h;
    let tray = st.tray;

    // Borrow the config fields in place — this runs every second, and the
    // old full `Config` clone allocated several Strings per pass just to
    // read six scalars. The shared borrows end when compute_placement
    // returns; everything below uses `st` freely again.
    let Some(pl) = taskbar::compute_placement(
        tray,
        w,
        h,
        &st.config.widget_side,
        st.config.window_offset_left,
        st.config.window_offset_top,
        st.config.taskbar_left_space_win11,
        st.config.taskbar_right_space_win11,
        st.config.avoid_overlap_with_widgets,
    ) else {
        return;
    };

    // Experimental embed mode: keep the widget as a child of the taskbar band
    // (Lyricify-style) instead of a topmost overlay. The transition hides the
    // window, so it must run before the placement pass repositions it.
    let want_embed = st.config.embed_into_taskbar && !st.embed_failed;
    if st.embedded != want_embed {
        if taskbar::set_taskbar_child(hwnd, pl.parent, want_embed) {
            st.embedded = want_embed;
            st.last_layout = None;
            eprintln!("razer-taskbar: embed={} applied", want_embed);
        } else {
            st.embed_failed = true;
            st.last_layout = None;
            eprintln!("razer-taskbar: embed rejected (SetParent), staying as overlay");
        }
    } else if st.embedded && !taskbar::is_child_of(hwnd, pl.parent) {
        // Explorer restart re-created the band and detached our window:
        // re-attach, or fall back to the overlay for this session.
        if taskbar::set_taskbar_child(hwnd, pl.parent, true) {
            st.last_layout = None;
            eprintln!("razer-taskbar: re-embedded after parent change");
        } else {
            st.embedded = false;
            st.embed_failed = true;
            st.last_layout = None;
            eprintln!("razer-taskbar: re-embed failed, falling back to overlay");
        }
    }

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
    let line = format!(
        "overlay kind={:?} pos=({sx},{sy}) size={w}x{h} embed={}",
        pl.kind, st.embedded
    );
    if st.last_log.as_deref() != Some(line.as_str()) {
        eprintln!("razer-taskbar: {line}");
        st.last_log = Some(line);
    }

    // Apply. move_overlay also re-asserts HWND_TOPMOST every pass (cheap
    // z-order re-claim against other topmost tools). A pure move needs no
    // repaint — layered window content survives — so invalidate only when
    // the rect actually changed.
    let changed = st.last_layout != Some((sx, sy, w, h));
    if st.embedded {
        // Children position in the band's CLIENT coordinates; the placement
        // math is in band coordinates, so only the client-origin offset
        // differs. HWND_TOP keeps the widget sibling #0 (above the XAML
        // bridge) — re-asserted per pass, a no-op when already first.
        let (cox, coy) = taskbar::client_origin(pl.parent);
        unsafe {
            let _ = SetWindowPos(
                hwnd,
                HWND_TOP,
                sx - cox,
                sy - coy,
                w,
                h,
                SWP_NOACTIVATE | SWP_SHOWWINDOW,
            );
        }
    } else {
        taskbar::move_overlay(hwnd, sx, sy, w, h);
    }
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
                    let embedded = state().map(|s| s.embedded).unwrap_or(false);
                    if embedded {
                        // Children have no TOPMOST band: riding above the XAML
                        // bridge is a sibling order instead, so re-assert it
                        // once a second in case the shell re-inserts children.
                        taskbar::reassert_child_top(hwnd);
                    } else {
                        // Detect "the taskbar raised itself above us" BEFORE
                        // the re-asserting placement pass, and arm a fast
                        // repair burst so we win the re-raise race (one pass
                        // per second alone is too slow and read as the widget
                        // vanishing).
                        if z_covered(hwnd) {
                            arm_z_burst(hwnd);
                        }
                    }
                    place_widget(hwnd);
                    tray::refresh();
                    // Battery changes never move the rect, so place_widget's
                    // changed-only invalidate would leave the digits frozen
                    // until the widget happens to move. Invalidate each
                    // second; paint() dedupes by signature, so an unchanged
                    // second costs one BeginPaint/EndPaint.
                    invalidate_widget();
                    // A layout request coalesced right before the poll tick
                    // should not wait for its own timer — flush it now.
                    flush_pending_layout(hwnd);
                }
                TIMER_LAYOUT => {
                    let _ = KillTimer(hwnd, TIMER_LAYOUT);
                    flush_pending_layout(hwnd);
                }
                TIMER_HOVER => {
                    // Piggyback two cheap checks on this 120ms tick:
                    // - z-order: recover from taskbar raises within ~120ms
                    //   (plain SetWindowPos(HWND_TOPMOST) per second loses
                    //   the race when the shell re-raises repeatedly) —
                    //   meaningless for an embedded child, which cannot
                    //   lose the band's z-order in the first place;
                    // - occupancy: occupant moves fire no UIA event, so the
                    //   fingerprint detector is what makes avoidance fast.
                    let embedded = state().map(|s| s.embedded).unwrap_or(false);
                    if !embedded && z_covered(hwnd) {
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
                        place_widget(hwnd);
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
        place_widget(hwnd);
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
        place_widget(hwnd);
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
    place_widget(hwnd);
}

/// Cached UI text font (Segoe UI Variable Text, falling back to Segoe UI),
/// keyed by (pixel height, weight). Lives until process exit like the icon
/// fonts — create/delete per repaint used to be this function's hot path.
/// Handles are stored as raw ints because HFONT is not Send/Sync; GDI font
/// handles are process-wide and usable from any thread.
fn cached_text_font(height: i32, weight: i32) -> HFONT {
    static CACHE: OnceLock<Mutex<HashMap<(i32, i32), isize>>> = OnceLock::new();
    let cache = CACHE.get_or_init(|| Mutex::new(HashMap::new()));
    let mut cache = cache.lock().unwrap();
    let handle = *cache.entry((height, weight)).or_insert_with(|| unsafe {
        let f = CreateFontW(
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
            ANTIALIASED_QUALITY.0 as u32,
            (DEFAULT_PITCH.0 | FF_DONTCARE.0) as u32,
            w!("Segoe UI Variable Text"),
        );
        // Fallback if the Variable font is missing (Win10 / older Win11).
        let f = if f.is_invalid() {
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
                ANTIALIASED_QUALITY.0 as u32,
                (DEFAULT_PITCH.0 | FF_DONTCARE.0) as u32,
                w!("Segoe UI"),
            )
        } else {
            f
        };
        f.0 as isize
    });
    HFONT(handle as *mut std::ffi::c_void)
}

/// Vertical shift (px) so a DT_VCENTER draw of `text` centers the string's
/// actual INK rather than its line box: line boxes carry asymmetric descent
/// padding, so digits and Fluent icons ride visibly high inside tight rows.
/// Union ink box of the string via GGO_METRICS, relative to the line box of
/// the font currently selected in `hdc`; 0 when metrics are unavailable.
unsafe fn ink_center_delta(hdc: HDC, text: &[u16]) -> i32 {
    let mut tm = TEXTMETRICW::default();
    if GetTextMetricsW(hdc, &mut tm).as_bool() && !text.is_empty() {
        let (mut top, mut depth) = (i32::MIN, i32::MIN);
        for &ch in text {
            let mut gm = GLYPHMETRICS::default();
            let mat = MAT2 {
                eM11: FIXED { fract: 0, value: 1 },
                eM12: FIXED { fract: 0, value: 0 },
                eM21: FIXED { fract: 0, value: 0 },
                eM22: FIXED { fract: 0, value: 1 },
            };
            if GetGlyphOutlineW(hdc, ch as u32, GGO_METRICS, &mut gm, 0, None, &mat)
                != GDI_ERROR as u32
            {
                top = top.max(gm.gmptGlyphOrigin.y);
                depth = depth.max(gm.gmBlackBoxY as i32 - gm.gmptGlyphOrigin.y);
            }
        }
        if top >= i32::MIN / 2 {
            return ((tm.tmDescent - tm.tmAscent) - (depth - top)) / 2;
        }
    }
    0
}

/// Single-line text at `x`, vertically centered in `rect` by its INK, over a
/// 1px drop shadow drawn first (never pure black — the color key would cut
/// it out). DrawTextW only writes back into the buffer with DT_MODIFYSTRING,
/// so one buffer serves both passes without cloning.
unsafe fn draw_shadowed_text(
    hdc: HDC,
    font: HFONT,
    text: &mut [u16],
    x: i32,
    rect: &RECT,
    color: COLORREF,
    shadow: COLORREF,
) {
    let _ = SelectObject(hdc, font);
    let dy = ink_center_delta(hdc, text);
    let mut r = RECT {
        left: x + 1,
        top: rect.top + 1 + dy,
        right: rect.right,
        bottom: rect.bottom + dy,
    };
    let _ = SetTextColor(hdc, shadow);
    let _ = DrawTextW(hdc, text, &mut r, DT_SINGLELINE | DT_VCENTER | DT_LEFT);
    let mut r = RECT {
        left: x,
        top: rect.top + dy,
        right: rect.right,
        bottom: rect.bottom + dy,
    };
    let _ = SetTextColor(hdc, color);
    let _ = DrawTextW(hdc, text, &mut r, DT_SINGLELINE | DT_VCENTER | DT_LEFT);
}

/// Two-row battery UI: row 1 = battery glyph + percentage, row 2 = status
/// icon (E823 clock while discharging, F607 bolt while charging) + predicted
/// time. The device-type icon stands alone, vertically centered across the
/// full widget height. Transparent background.
///
/// Design (v3): the prediction stacks as a second, smaller row instead of
/// widening the widget; without one (feature off, no history yet, device
/// disconnected) row 1 is the only row, vertically centered, with the type
/// icon back inside the single-row group. No chip background — the black
/// color key keeps the window transparent so TTB acrylic / taskbar texture
/// shows through. Every glyph and text gets a soft drop shadow for
/// readability over busy backdrops; geometry is measured from real widths.
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
    let saver = device.as_ref().map(|d| d.battery_saver).unwrap_or(false);
    let connected = device.as_ref().map(|d| d.is_connected).unwrap_or(false);
    // Row 1: percentage (dim "--" with no device). Row 2: the bare duration
    // (the row's status icon conveys charging) — needs the feature on, a
    // connected device, and an estimate derived from recorded history.
    let top_label = device
        .as_ref()
        .filter(|_| connected)
        .map(|d| format!("{}%", d.battery_percentage))
        .unwrap_or_else(|| "--".into());
    let bottom_label = if st.config.show_estimated_time && connected {
        device
            .as_ref()
            .and_then(|d| crate::history::estimate_for(&d.handle))
            .map(crate::history::format_estimate_plain)
    } else {
        None
    };
    let sig = PaintSig {
        top: top_label.clone(),
        bottom: bottom_label.clone().unwrap_or_default(),
        level,
        charging,
        saver,
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
        let hdc = BeginPaint(hwnd, &mut ps);
        if hdc.is_invalid() {
            return;
        }
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

        // Palette: white glyph/text, dim gray secondary row, dark-gray
        // shadow. Shadow must NOT be pure black — it would match the color
        // key and be cut out. Grayscale (not ClearType) antialiasing avoids
        // colored subpixel fringe around glyphs.
        let fg_color = COLORREF(0x00FFFFFF);
        let dim = COLORREF(0x00B0B0B0);
        let shadow = COLORREF(0x00202020);

        // Fonts are process-cached (icons::icon_font / cached_text_font):
        // four CreateFontW + two DeleteObject per repaint used to run here.
        // Native Win11 glyphs: Segoe Fluent Icons battery (EBA0 series);
        // icon fonts snap to Microsoft's magic pixel sizes (16/20/24/…)
        // for crisp rendering. Row 1 is half the window now, so the glyph
        // and percentage scale down from the single-row-era sizes.
        let icon_h = crate::icons::snap_size((20.0 * scale).round() as i32);
        let hicon_font = crate::icons::icon_font(icon_h);
        let hfont = cached_text_font((14.0 * scale).round() as i32, FW_SEMIBOLD.0 as i32);
        let old_font = SelectObject(hdc, hfont);

        let (fr, fg, fb) = if connected {
            color_for(level)
        } else {
            (0x80, 0x80, 0x80)
        };
        let text_color = if connected { fg_color } else { dim };
        let mut wide: Vec<u16> = top_label.encode_utf16().collect();
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
        // Measure the native icon glyph the same way. Glyph series by state
        // (Win11 per-level sets, 11 glyphs each): normal EBA0-EBAA, charging
        // bolt EBAB-EBB5, saver leaf EBB6-EBC0. A disconnected device draws
        // the plain (gray) normal glyph.
        let glyph_state = if !connected {
            crate::battery::BatteryGlyphState::Normal
        } else if saver {
            crate::battery::BatteryGlyphState::Saver
        } else if charging {
            crate::battery::BatteryGlyphState::Charging
        } else {
            crate::battery::BatteryGlyphState::Normal
        };
        let mut icon_ch = [crate::battery::battery_glyph(level, glyph_state) as u16];
        let mut icon_measure = RECT {
            left: 0,
            top: 0,
            right: 0,
            bottom: 0,
        };
        let _ = SelectObject(hdc, hicon_font);
        DrawTextW(
            hdc,
            &mut icon_ch,
            &mut icon_measure,
            DT_SINGLELINE | DT_CALCRECT | DT_LEFT,
        );
        let icon_w = (icon_measure.right - icon_measure.left).max(1);
        let _ = SelectObject(hdc, hfont);
        // Row split: two stacked half-height rows while a prediction shows,
        // otherwise row 1 spans the full height (the old single-row look).
        let two_rows = bottom_label.is_some();
        let top_rect = RECT {
            left: 0,
            top: 0,
            right: w,
            bottom: if two_rows { h / 2 } else { h },
        };

        // Row-2 content, measured up front so the column geometry can use
        // it: [status icon] [gap] [time]. The discharging prediction gets
        // the recent/clock glyph (E823), charging gets the charge glyph
        // (F607) — Segoe Fluent Icons, same MDL2 fallback. The fonts are
        // cached lookups, so they are created unconditionally and reused
        // by the draw pass below.
        let mut est_ch = [0u16; 1];
        let mut est_wide: Vec<u16> = Vec::new();
        let mut est_icon_w = 0;
        let mut est_w = 0;
        let est_icon_font =
            crate::icons::icon_font(crate::icons::snap_size((14.0 * scale).round() as i32));
        let est_font = cached_text_font((14.0 * scale).round() as i32, FW_NORMAL.0 as i32);
        if let Some(est_text) = bottom_label.as_deref() {
            est_ch[0] = if charging { '\u{F607}' } else { '\u{E823}' } as u16;
            est_wide = est_text.encode_utf16().collect();
            let _ = SelectObject(hdc, est_icon_font);
            let mut m = RECT { left: 0, top: 0, right: 0, bottom: 0 };
            DrawTextW(hdc, &mut est_ch, &mut m, DT_SINGLELINE | DT_CALCRECT | DT_LEFT);
            est_icon_w = (m.right - m.left).max(1);
            let _ = SelectObject(hdc, est_font);
            let mut m = RECT { left: 0, top: 0, right: 0, bottom: 0 };
            DrawTextW(hdc, &mut est_wide, &mut m, DT_SINGLELINE | DT_CALCRECT | DT_LEFT);
            est_w = (m.right - m.left).max(1);
            let _ = SelectObject(hdc, hfont);
        }

        // Geometry. Two rows: the type icon stands alone on the left,
        // vertically centered across the FULL widget height (not tied to
        // row 1). The two rows form a column with a shared ICON COLUMN: the
        // bigger battery glyph and the smaller status icon center on one
        // vertical axis, and both texts start at the same x — naive
        // left-alignment made the narrow clock look off-center next to the
        // wide battery glyph. Single row: the old centered [type icon]
        // [glyph] [percentage] group. The type icon is omitted while no
        // device is shown ("--").
        let gap = (5.0 * scale).round() as i32;
        let kind = device.as_ref().map(|d| d.kind);
        let kind_h = (14.0 * scale).round() as i32;
        let kind_w = kind
            .map(|k| crate::icons::width_for(hdc, kind_h, k))
            .unwrap_or(0);
        let kind_gap = if kind.is_some() { gap } else { 0 };
        let (group_x, icon_x, text_x, est_icon_x, est_text_x) = if two_rows {
            let icon_col_w = icon_w.max(est_icon_w);
            let row1_w = icon_col_w + gap + text_w;
            let row2_w = icon_col_w + gap + est_w;
            let group_w = kind_w + kind_gap + row1_w.max(row2_w);
            let gx = (w - group_w) / 2;
            let cx = gx + kind_w + kind_gap;
            let tx = cx + icon_col_w + gap;
            (
                gx,
                cx + (icon_col_w - icon_w) / 2,
                tx,
                cx + (icon_col_w - est_icon_w) / 2,
                tx,
            )
        } else {
            let group_w = kind_w + kind_gap + icon_w + gap + text_w;
            let gx = (w - group_w) / 2;
            let ix = gx + kind_w + kind_gap;
            (gx, ix, ix + icon_w + gap, 0, 0)
        };
        if let Some(k) = kind {
            let kc = if connected {
                (0xFF, 0xFF, 0xFF)
            } else {
                (0x80, 0x80, 0x80)
            };
            let kind_y = (h - kind_h) / 2;
            crate::icons::draw(hdc, group_x, kind_y, kind_h, k, kc);
        }
        // Level-tinted battery glyph with drop shadow.
        let icon_rgb = if connected {
            (fr, fg, fb)
        } else {
            (0x80, 0x80, 0x80)
        };
        let icon_color =
            COLORREF(icon_rgb.0 as u32 | ((icon_rgb.1 as u32) << 8) | ((icon_rgb.2 as u32) << 16));
        draw_shadowed_text(hdc, hicon_font, &mut icon_ch, icon_x, &top_rect, icon_color, shadow);
        draw_shadowed_text(hdc, hfont, &mut wide, text_x, &top_rect, text_color, shadow);

        // Row 2: the prediction with its status icon, left-aligned under
        // row 1, dimmed while discharging (white while charging). Icon and
        // text share the row rect, so DT_VCENTER centers both on the same
        // optical line.
        if two_rows {
            let est_rect = RECT {
                left: 0,
                top: h / 2,
                right: w,
                bottom: h,
            };
            let est_color = if charging { fg_color } else { dim };
            draw_shadowed_text(
                hdc,
                est_icon_font,
                &mut est_ch,
                est_icon_x,
                &est_rect,
                est_color,
                shadow,
            );
            draw_shadowed_text(
                hdc,
                est_font,
                &mut est_wide,
                est_text_x,
                &est_rect,
                est_color,
                shadow,
            );
        }
        let _ = SelectObject(hdc, old_font);
        st.painted_sig = Some(sig);
        let _ = EndPaint(hwnd, &ps);
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
