//! Taskbar discovery, Win10/Win11 placement, and coexistence with other hook tools.
//!
//! Verified against TrafficMonitor (`TaskBarDlg.cpp` / `Win11TaskbarDlg.cpp` /
//! `ClassicalTaskbarDlg.cpp`) and a live Spy++-style enumeration on this machine:
//!
//! ```text
//! Shell_TrayWnd 0x1016A  0,1552-2560,1600 (2560x48)
//!   Start                              0-55
//!   ReBarWindow32 / MSTaskSwWClass     55-583
//!   TrayNotifyWnd                      2171-2560
//!   DesktopWindowContentBridge         full-width overlay
//!   free gap                           583-2171
//! ```
//!
//! Rules implemented here:
//! - Win11 (`DesktopWindowContentBridge` present): anchor at
//!   `TrayNotifyWnd.left - width + 2`, vertically centered on `Start`.
//! - Classic/Win10: anchor relative to the `ReBarWindow32` band (fallback
//!   `WorkerW`, last resort the tray itself).
//! - No third-party avoidance by design: the widget sits at its anchor and
//!   draws above competing widgets (embed mode = band sibling #0, overlay
//!   mode = TOPMOST). The former occupant sampling / hold / grace /
//!   jump-cap machinery was removed — fast-changing neighbours (lyrics
//!   windows) made the chase pointless and jittery.

use std::sync::Mutex;
use std::time::{Duration, Instant};

use windows::core::{w, BSTR, VARIANT};
use windows::Win32::Foundation::{HWND, POINT, RECT};
use windows::Win32::Graphics::Gdi::ClientToScreen;
use windows::Win32::UI::WindowsAndMessaging::*;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TaskbarKind {
    Win11,
    Classic,
}

pub fn find_shell_tray() -> Option<HWND> {
    unsafe {
        let hwnd = FindWindowW(w!("Shell_TrayWnd"), None).unwrap_or(HWND(std::ptr::null_mut()));
        if hwnd.0.is_null() {
            None
        } else {
            Some(hwnd)
        }
    }
}

pub fn find_child(parent: HWND, class: &str) -> Option<HWND> {
    unsafe {
        let wide: Vec<u16> = class.encode_utf16().chain(std::iter::once(0)).collect();
        let hwnd =
            FindWindowExW(parent, None, windows::core::PCWSTR(wide.as_ptr()), None).unwrap_or(HWND(std::ptr::null_mut()));
        if hwnd.0.is_null() {
            None
        } else {
            Some(hwnd)
        }
    }
}

pub fn window_rect(hwnd: HWND) -> Option<RECT> {
    unsafe {
        let mut rect = RECT::default();
        GetWindowRect(hwnd, &mut rect).ok()?;
        Some(rect)
    }
}

pub fn is_window_visible(hwnd: HWND) -> bool {
    unsafe { IsWindowVisible(hwnd).as_bool() }
}

/// Win11 detection: `DesktopWindowContentBridge` child exists (TrafficMonitor parity).
pub fn detect_kind(tray: HWND) -> TaskbarKind {
    if find_child(tray, "Windows.UI.Composition.DesktopWindowContentBridge").is_some() {
        TaskbarKind::Win11
    } else {
        TaskbarKind::Classic
    }
}

pub fn taskbar_rect(tray: HWND) -> Option<RECT> {
    window_rect(tray)
}

#[derive(Debug, Clone)]
pub struct Placement {
    /// Band the returned x/y are relative to (Shell_TrayWnd on Win11, the
    /// ReBarWindow32 band on Classic) — the caller converts to screen coords.
    pub parent: HWND,
    pub x: i32,
    pub y: i32,
    #[allow(dead_code)]
    pub w: i32,
    #[allow(dead_code)]
    pub h: i32,
    pub kind: TaskbarKind,
}

/// Compute widget placement inside the taskbar band.
///
/// - `w/h`: desired widget size (physical px, already DPI-scaled by caller).
/// - Right side (default): `x = notify.left - w + 2`; the widgets board
///   (weather) reserve is a hard limit — the board is OS chrome, not a
///   third-party widget, and overlapping it is never acceptable.
/// - Left side: anchored after `Start`, keeping the reserved icons zone.
///
/// No third-party avoidance by design: competing widgets (Lyricify,
/// TrafficMonitor, ...) may overlap us and we draw above them (embed mode
/// keeps us as sibling #0 of the band, overlay mode stays TOPMOST). The
/// former hold/grace/jump-cap/occupant-sampling machinery is gone —
/// fast-changing neighbours (lyrics windows) made the chase pointless.
pub fn compute_placement(
    tray: HWND,
    w: i32,
    h: i32,
    side: &str,
    offset_left: i32,
    offset_top: i32,
    left_space_win11: i32,
    right_space_fallback: i32,
    avoid_widgets: bool,
) -> Option<Placement> {
    let kind = detect_kind(tray);
    let bar = taskbar_rect(tray)?;
    let bar_w = bar.right - bar.left;
    let bar_h = bar.bottom - bar.top;

    match kind {
        TaskbarKind::Win11 => {
            let notify = find_child(tray, "TrayNotifyWnd").and_then(window_rect);
            // NOTE: the legacy `Start` HWND is hidden (zero-size or invisible)
            // on Win11 — ignore it and fall back to the taskbar band, otherwise
            // its stale 55px rect pushes a left-side widget needlessly right.
            let start = find_child(tray, "Start")
                .filter(|h| is_window_visible(*h))
                .and_then(window_rect)
                .filter(|r| r.right - r.left > 0 && r.bottom - r.top > 0);
            let notify_left = notify
                .map(|r| r.left - bar.left)
                .unwrap_or(bar_w - right_space_win11(right_space_fallback));
            let start_h = start.map(|r| r.bottom - r.top).unwrap_or(bar_h);
            let y = (start_h - h) / 2 + (bar_h - start_h) + offset_top;

            // The weather/widgets board is NOT an HWND — it is XAML content
            // inside the bridge, so `EnumChildWindows` never sees it. Its UIA
            // rect is the ground truth; fall back to the registry estimate
            // only when UIA is unavailable.
            //
            // The board sits just LEFT of TrayNotifyWnd: the reserved zone is
            // [board_left, notify_left) and right-anchored widgets must stop
            // at board_left. NOTE: this reserve is UNCONDITIONAL — the board
            // is OS chrome; overlapping it is never acceptable.
            let board = widgets_button_rect();
            let board_left = board.map(|r| r.left - bar.left);
            let start_left = start.map(|r| r.left - bar.left).unwrap_or(0);
            let start_w = start.map(|r| r.right - r.left).unwrap_or(0);
            let mut min_x = start_left + start_w + 2 + widgets_zone_width();
            if avoid_widgets {
                min_x = min_x.max(start_left + start_w + 2 + left_space_win11.max(0));
            }
            // Right-side usable band ends where the board begins (when known).
            let mut max_right = notify_left + 2;
            if side != "left" {
                if let Some(bl) = board_left {
                    max_right = max_right.min(bl - 2);
                }
            }

            let mut x = anchor_x_win11(side, max_right - 2, min_x, w) + offset_left;
            // The board reserve is a hard floor for LEFT-anchored widgets:
            // clamp() alone could push us back onto the weather board when
            // the band is crowded, so re-assert min_x afterwards.
            if side == "left" {
                x = x.max(min_x);
            }
            x = x.clamp(2, (bar_w - w - 2).max(2));

            Some(Placement {
                parent: tray,
                x,
                y,
                w,
                h,
                kind,
            })
        }
        TaskbarKind::Classic => {
            // Anchor on the ReBarWindow32 band (WorkerW fallback, tray last
            // resort); the returned x/y are relative to that band and
            // pl.parent tells the caller so.
            let rebar = find_child(tray, "ReBarWindow32")
                .or_else(|| find_child(tray, "WorkerW"))
                .unwrap_or(tray);
            let band = window_rect(rebar).unwrap_or(bar);
            let band_w = band.right - band.left;
            let band_h = band.bottom - band.top;
            let mut x = anchor_x_classic(side, band_w, w) + offset_left;
            x = x.clamp(2, (band_w - w - 2).max(2));
            let y = (band_h - h) / 2 + offset_top;
            Some(Placement {
                parent: rebar,
                x,
                y,
                w,
                h,
                kind,
            })
        }
    }
}

/// Ideal anchor for Win11 bands.
fn anchor_x_win11(side: &str, notify_left: i32, min_x: i32, w: i32) -> i32 {
    if side == "left" {
        min_x
    } else {
        // Right anchor stops at the widgets board (passed in as
        // `notify_left` = min(board_left, tray_notify_left)); the board is
        // XAML content the HWND enumeration never sees, so the anchor edge
        // is the ONLY thing keeping us off it.
        notify_left - w + 2
    }
}

/// Ideal anchor for Classic bands.
fn anchor_x_classic(side: &str, band_w: i32, w: i32) -> i32 {
    if side == "left" {
        2
    } else {
        band_w - w - 2
    }
}



fn right_space_win11(fallback: i32) -> i32 {
    fallback.max(0)
}

/// Cached UIA rect of the Win11 widgets board button (see `widgets_button_rect`).
struct WidgetsRectCache {
    rect: Option<RECT>,
    /// The board sits just left of TrayNotifyWnd: a changed notify edge means
    /// the board moved, so the cache must not survive it.
    notify_left: i32,
    seen: Instant,
}

/// How long the widgets board UIA rect stays valid before re-querying.
const WIDGETS_CACHE_TTL: Duration = Duration::from_secs(30);

fn widgets_cache() -> &'static Mutex<Option<WidgetsRectCache>> {
    use std::sync::OnceLock;
    static CACHE: OnceLock<Mutex<Option<WidgetsRectCache>>> = OnceLock::new();
    CACHE.get_or_init(|| Mutex::new(None))
}

/// Drop the cached widgets board rect (taskbar rebuilt → `TaskbarCreated`).
pub fn invalidate_widgets_cache() {
    if let Ok(mut c) = widgets_cache().lock() {
        *c = None;
    }
}

/// Screen rect of the Win11 widgets board (weather) button, via UIA.
///
/// The board is NOT an HWND occupant — it is XAML content inside the
/// `DesktopWindowContentBridge`, so `EnumChildWindows` never sees it. The
/// only reliable position source is the UIA tree (`WidgetsButton` under
/// `TaskbarFrame`). Returns `None` when UIA is unavailable (caller falls
/// back to the registry-based estimate).
///
/// The UIA query is expensive (CoCreateInstance + engine-side tree search)
/// and the button only moves when the notify/tray geometry moves, so results
/// are cached for `WIDGETS_CACHE_TTL` or until the notify edge shifts. When
/// the board is disabled (registry `TaskbarDa=0`) the search is skipped.
pub fn widgets_button_rect() -> Option<RECT> {
    // Registry first: with the board off, UIA would report nothing — skip
    // the search (registry flips are picked up live on the next placement).
    if !widgets_shown() {
        return None;
    }
    let notify_left = find_shell_tray()
        .and_then(|tray| find_child(tray, "TrayNotifyWnd"))
        .and_then(window_rect)
        .map(|r| r.left)
        .unwrap_or(0);
    if let Ok(cache) = widgets_cache().lock() {
        if let Some(c) = cache.as_ref() {
            if c.notify_left == notify_left && c.seen.elapsed() < WIDGETS_CACHE_TTL {
                return c.rect;
            }
        }
    }
    let rect = widgets_button_rect_uia();
    if let Ok(mut cache) = widgets_cache().lock() {
        *cache = Some(WidgetsRectCache {
            rect,
            notify_left,
            seen: Instant::now(),
        });
    }
    rect
}

/// Uncached UIA search for the `WidgetsButton` rect (see `widgets_button_rect`).
fn widgets_button_rect_uia() -> Option<RECT> {
    use windows::Win32::System::Com::*;
    use windows::Win32::UI::Accessibility::*;
    unsafe {
        // UIA must live on an STA thread; our caller runs on the UI thread
        // which is already STA. If COM is not initialized here, bail out
        // instead of disturbing the host threading model.
        let uia: IUIAutomation =
            CoCreateInstance(&CUIAutomation, None, CLSCTX_INPROC_SERVER).ok()?;
        let tray = find_shell_tray()?;
        let root = uia.ElementFromHandle(tray).ok()?;
        // Engine-side descendant search. A manual ControlViewWalker DFS used
        // to do this job, but the walker dead-ends at the tray root whenever
        // the shell's control view hiccups (observed live on 26340: the
        // walker returned no children at all while a plain descendant search
        // still saw the whole tree), so the board reserve silently vanished.
        // FindFirst pushes the traversal into the provider, which keeps
        // working when the walker view does not.
        let cond = uia
            .CreatePropertyCondition(
                UIA_AutomationIdPropertyId,
                &VARIANT::from(BSTR::from("WidgetsButton")),
            )
            .ok()?;
        let el = root.FindFirst(TreeScope_Descendants, &cond).ok()?;
        let r = el.CurrentBoundingRectangle().ok()?;
        Some(RECT { left: r.left, top: r.top, right: r.right, bottom: r.bottom })
    }
}

/// Width of the Win11 widgets board (weather) zone on the left edge.
/// Measured from the user's screenshot (~150px at 96 DPI for icon + "28C" text).
/// Used as a reserved zone so the widget never overlaps it.
fn widgets_zone_width() -> i32 {
    if widgets_shown() { 160 } else { 0 }
}

pub fn widgets_shown() -> bool {
    read_dword(
        "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced",
        "TaskbarDa",
    )
    .unwrap_or(1)
        != 0
}

fn read_dword(subkey: &str, value: &str) -> Option<u32> {
    use windows::Win32::System::Registry::*;
    unsafe {
        let subkey_w: Vec<u16> = subkey.encode_utf16().chain(std::iter::once(0)).collect();
        let value_w: Vec<u16> = value.encode_utf16().chain(std::iter::once(0)).collect();
        let mut key = Default::default();
        if RegOpenKeyExW(
            HKEY_CURRENT_USER,
            windows::core::PCWSTR(subkey_w.as_ptr()),
            0,
            KEY_READ,
            &mut key,
        )
        .is_err()
        {
            return None;
        }
        let mut data = 0u32;
        let mut len = std::mem::size_of::<u32>() as u32;
        let ok = RegQueryValueExW(
            key,
            windows::core::PCWSTR(value_w.as_ptr()),
            None,
            None,
            Some(&mut data as *mut u32 as *mut u8),
            Some(&mut len),
        )
        .is_ok();
        let _ = RegCloseKey(key);
        ok.then_some(data)
    }
}

/// `true` when `tray` sits ABOVE `widget` in z-order — i.e. the taskbar
/// visually covers the widget. Both windows normally live in the topmost
/// band; whichever was raised last paints over the other, and the shell
/// raises Shell_TrayWnd repeatedly (tray icon churn, animations, settings
/// changes). Walking a bounded `GW_HWNDPREV` chain is a few syscalls, cheap
/// enough for the 1s tick.
pub fn tray_above(widget: HWND, tray: HWND) -> bool {
    unsafe {
        let mut cur = GetWindow(widget, GW_HWNDPREV).unwrap_or_default();
        let mut hops = 0;
        while !cur.0.is_null() && hops < 16 {
            if cur == tray {
                return true;
            }
            cur = GetWindow(cur, GW_HWNDPREV).unwrap_or_default();
            hops += 1;
        }
        false
    }
}

/// Position a top-level overlay above the taskbar (Taskbar-Lyrics style).
/// HWND_TOPMOST + SWP_NOACTIVATE: floats above the (possibly acrylic)
/// taskbar without stealing focus. SWP_NOOWNERZORDER | SWP_NOSENDCHANGING
/// mirror Taskbar-Lyrics (no owner-z interplay, no changing round-trip).
pub fn move_overlay(widget: HWND, x: i32, y: i32, w: i32, h: i32) {
    unsafe {
        // SWP_NOOWNERZORDER | SWP_NOSENDCHANGING mirror Taskbar-Lyrics:
        // no owner-z interplay, no WM_WINDOWPOSCHANGING round-trip.
        let _ = SetWindowPos(
            widget,
            HWND_TOPMOST,
            x,
            y,
            w,
            h,
            SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING | SWP_SHOWWINDOW,
        );
    }
}

/// Experimental embed mode (Lyricify's taskbar lyrics): reparent `widget`
/// into the taskbar band `parent` as a plain WS_CHILD — layered + no-activate
/// are kept, TOPMOST is dropped (children have no TOPMOST band) — and park it
/// at sibling index 0 so it draws above the XAML bridge. A child rides every
/// shell raise for free, which removes the whole topmost-race machinery.
/// Unsupported and update-fragile; the caller falls back to the overlay when
/// this returns false (some security software blocks cross-process SetParent).
pub fn set_taskbar_child(widget: HWND, parent: HWND, embed: bool) -> bool {
    unsafe {
        let _ = ShowWindow(widget, SW_HIDE);
        if embed {
            let ex = GetWindowLongPtrW(widget, GWL_EXSTYLE);
            let _ = SetWindowLongPtrW(widget, GWL_EXSTYLE, ex & !(WS_EX_TOPMOST.0 as isize));
            if SetParent(widget, parent).is_err() {
                // Restore exactly what was there and let the caller stay
                // in overlay mode.
                let _ = SetWindowLongPtrW(widget, GWL_EXSTYLE, ex);
                let _ = ShowWindow(widget, SW_SHOWNA);
                return false;
            }
            let style = GetWindowLongPtrW(widget, GWL_STYLE);
            let _ = SetWindowLongPtrW(
                widget,
                GWL_STYLE,
                (style & !(WS_POPUP.0 as isize)) | (WS_CHILD.0 as isize),
            );
            // Sibling #0 — above DesktopWindowContentBridge. Among children
            // this relative order is all the protection a shell raise cannot
            // take away, so re-assert it on placement passes.
            let _ = SetWindowPos(
                widget,
                HWND_TOP,
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW,
            );
        } else {
            let style = GetWindowLongPtrW(widget, GWL_STYLE);
            let _ = SetWindowLongPtrW(
                widget,
                GWL_STYLE,
                (style & !(WS_CHILD.0 as isize)) | (WS_POPUP.0 as isize),
            );
            let ex = GetWindowLongPtrW(widget, GWL_EXSTYLE);
            let _ = SetWindowLongPtrW(widget, GWL_EXSTYLE, ex | (WS_EX_TOPMOST.0 as isize));
            let _ = SetParent(widget, HWND(std::ptr::null_mut()));
        }
        true
    }
}

/// `true` while `widget` is parented into `parent` (embed mode's live check —
/// explorer restarts re-create the band, detaching our window from it).
pub fn is_child_of(widget: HWND, parent: HWND) -> bool {
    unsafe { GetAncestor(widget, GA_PARENT) == parent }
}

/// Origin of `parent`'s client area in screen coordinates. Child windows
/// position in parent-client coordinates; the placement math is in band
/// coordinates, so the difference is the only conversion embed mode needs.
pub fn client_origin(parent: HWND) -> (i32, i32) {
    unsafe {
        let mut pt = POINT::default();
        if ClientToScreen(parent, &mut pt).as_bool() {
            (pt.x, pt.y)
        } else {
            window_rect(parent).map(|r| (r.left, r.top)).unwrap_or((0, 0))
        }
    }
}

/// Re-assert sibling #0 for an embedded widget (children are ordered only
/// among themselves; whoever the shell inserts lands below us again).
pub fn reassert_child_top(widget: HWND) {
    unsafe {
        let _ = SetWindowPos(
            widget,
            HWND_TOP,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE,
        );
    }
}
