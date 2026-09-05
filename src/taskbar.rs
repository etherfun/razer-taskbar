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
//! - Coexistence: `EnumChildWindows` over the parent, skip the OS whitelist,
//!   shift past any visible non-zero occupant (e.g. TrafficMonitor) — but
//!   only when our LIVE rect actually collides. If we are already placed and
//!   collision-free, the occupant already yields to us (it placed itself
//!   around us), so we hold position instead of leapfrogging left.

use std::collections::HashMap;
use std::sync::Mutex;
use std::time::{Duration, Instant};

use windows::core::w;
use windows::Win32::Foundation::{BOOL, HWND, LPARAM, POINT, RECT};
use windows::Win32::Graphics::Gdi::ClientToScreen;
use windows::Win32::UI::WindowsAndMessaging::*;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TaskbarKind {
    Win11,
    Classic,
}

#[derive(Debug, Clone)]
pub struct Occupant {
    pub hwnd: HWND,
    pub rect: RECT,
    /// Tight visible-content bounds inside `rect` (screen coords), shrunk
    /// by sampling the screen for pixels that differ from the taskbar
    /// background. Transparent layered windows (e.g. Lyricify's 984px
    /// window with ~400px of lyrics on the right) only block this part.
    /// `None` = sampling failed; fall back to the full `rect`.
    pub content: Option<RECT>,
    pub class: String,
    pub exe: String,
    /// `true` once this occupant has ever been observed moving while
    /// continuously visible (sticky) — i.e. it has its own avoidance logic
    /// and gets out of the way by itself.
    pub moved: bool,
    /// `true` on first sighting: it may still be running its own avoidance
    /// pass, so give it a grace period before we yield to it.
    pub is_new: bool,
    /// `true` when this occupant actually overlaps our live rect.
    pub blocks_us: bool,
}

/// Per-occupant memory: last seen rect + timestamp. Used to detect whether
/// the other widget moves on its own (has avoidance logic) vs. sits still.
///
/// Keyed by (pid, class, height-bucket): HWND values get reused after a
/// window dies, so a bare-HWND key would misread a brand-new window as
/// "moved". (pid, class) survives recreation; the height bucket keeps two
/// same-class widgets of different sizes from aliasing each other.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
struct OccupantKey {
    pid: u32,
    class: String,
    h_bucket: i32,
}

#[derive(Debug, Clone, Copy)]
struct OccupantMemory {
    left: i32,
    top: i32,
    right: i32,
    bottom: i32,
    seen: Instant,
    ever_moved: bool,
}

fn occupant_memories() -> &'static Mutex<HashMap<OccupantKey, OccupantMemory>> {
    use std::sync::OnceLock;
    static MEM: OnceLock<Mutex<HashMap<OccupantKey, OccupantMemory>>> = OnceLock::new();
    MEM.get_or_init(|| Mutex::new(HashMap::new()))
}

/// How long an unseen occupant stays in memory before being forgotten.
const OCCUPANT_MEMORY_TTL: Duration = Duration::from_secs(30);
/// Rect drift below this is treated as jitter, not a deliberate move.
const OCCUPANT_MOVE_EPS: i32 = 2;

fn occupant_key(o: &Occupant) -> OccupantKey {
    OccupantKey {
        pid: pid_of(o.hwnd),
        class: o.class.clone(),
        h_bucket: (o.rect.bottom - o.rect.top) / 8,
    }
}

/// OS-owned classes that are never treated as competing widgets.
///
/// NOTE: `Windows.UI.Composition.DesktopWindowContentBridge` is the Win11 XAML
/// overlay that covers the whole taskbar — it must stay whitelisted, otherwise
/// every placement would "yield" to it. Same for the zero-size input hosts.
const WHITELIST: &[&str] = &[
    "Start",
    "ReBarWindow32",
    "MSTaskSwWClass",
    "MSTaskListWClass",
    "TrayNotifyWnd",
    "Windows.UI.Composition.DesktopWindowContentBridge",
    "Windows.UI.Input.InputSite.WindowClass",
    "Windows.UI.Core.CoreWindow",
    "TrayDummySearchControl",
    "WorkerW",
    "Shell_TrayWnd",
    // Win11 widgets board (weather, 28C screenshot): full-height band on the
    // left when TaskbarDa=1. Treated as a reserved zone, not an occupant.
    "Windows.UI.Composition.DesktopWindowContentBridge_Widgets",
    "Widget",
    "Widgets",
];

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

pub fn class_name(hwnd: HWND) -> String {
    unsafe {
        let mut buf = [0u16; 256];
        let len = GetClassNameW(hwnd, &mut buf);
        String::from_utf16_lossy(&buf[..len as usize])
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

/// Process image name for an occupant window (for coexistence logging/menu).
pub fn exe_for_window(hwnd: HWND) -> String {
    unsafe {
        let mut pid = 0u32;
        GetWindowThreadProcessId(hwnd, Some(&mut pid));
        if pid == 0 {
            return String::new();
        }
        let handle = windows::Win32::System::Threading::OpenProcess(
            windows::Win32::System::Threading::PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            pid,
        );
        let Ok(handle) = handle else {
            return String::new();
        };
        let mut buf = [0u16; 260];
        let mut len = buf.len() as u32;
        let ok = windows::Win32::System::Threading::QueryFullProcessImageNameW(
            handle,
            windows::Win32::System::Threading::PROCESS_NAME_WIN32,
            windows::core::PWSTR(buf.as_mut_ptr()),
            &mut len,
        );
        let _ = windows::Win32::Foundation::CloseHandle(handle);
        if ok.is_err() {
            return String::new();
        }
        let full = String::from_utf16_lossy(&buf[..len as usize]);
        full.rsplit(['\\', '/']).next().unwrap_or("").to_owned()
    }
}

struct EnumCtx {
    parent: HWND,
    self_hwnd: *mut std::ffi::c_void,
    out: Vec<Occupant>,
}

/// PID of this process — occupants from our own process (e.g. a stale
/// widget window from a previous placement pass) must never count.
fn own_pid() -> u32 {
    std::process::id()
}

fn pid_of(hwnd: HWND) -> u32 {
    unsafe {
        let mut pid = 0u32;
        GetWindowThreadProcessId(hwnd, Some(&mut pid));
        pid
    }
}

unsafe extern "system" fn enum_proc(hwnd: HWND, lparam: LPARAM) -> BOOL {
    let ctx = &mut *(lparam.0 as *mut EnumCtx);
    if hwnd.0 == ctx.self_hwnd {
        return true.into();
    }
    if !is_window_visible(hwnd) {
        return true.into();
    }
    let class = class_name(hwnd);
    if WHITELIST.iter().any(|w| *w == class) {
        return true.into();
    }
    // Same class as our widget but missed the hwnd check (e.g. recreated
    // window): skip anything owned by our own process.
    if class == "RazerTaskbarWidget" || pid_of(hwnd) == own_pid() {
        return true.into();
    }
    let Some(rect) = window_rect(hwnd) else {
        return true.into();
    };
    if rect.right - rect.left <= 0 || rect.bottom - rect.top <= 0 {
        return true.into();
    }
    // Must intersect the parent taskbar band to count as an occupant.
    if let Some(band) = window_rect(ctx.parent) {
        let intersects = rect.left < band.right
            && rect.right > band.left
            && rect.top < band.bottom
            && rect.bottom > band.top;
        if !intersects {
            return true.into();
        }
    }
    ctx.out.push(Occupant {
        hwnd,
        rect,
        content: None, // filled by shrink_to_content() after enumeration
        class,
        exe: exe_for_window(hwnd),
        moved: false,
        is_new: false,
        blocks_us: false,
    });
    true.into()
}

/// Effective blocking rect: tight content bounds when known, else full rect.
fn block_rect(o: &Occupant) -> RECT {
    o.content.unwrap_or(o.rect)
}

/// Shrink each occupant's blocking rect to its visible (non-background)
/// content by sampling the screen.
///
/// Layered overlay widgets often own a much wider window than what they
/// draw (Lyricify: 984px window, ~400px of lyrics right-aligned, rest fully
/// transparent). Avoiding the full `GetWindowRect` wastes the transparent
/// part; avoiding only the drawn part lets neighbours pack into the gap.
///
/// `self_hwnd` is hidden during sampling so our own pixels never pollute
/// the measurement (otherwise our widget would look like the occupant's
/// content and we'd chase ourselves left once we sit inside its window).
/// Results are cached per occupant key and refreshed at most every 5s —
/// lyrics text changes don't move the content box materially, and this
/// keeps the steady-state cost at zero BitBlts per tick.
fn shrink_to_content(occupants: &mut [Occupant], self_hwnd: HWND) {
    // Hide ourselves only while sampling an occupant whose rect overlaps our
    // live rect (only there can our pixels pollute the measurement). When we
    // are collision-free — the normal steady state — a hide/show cycle is
    // pure flicker: every content-cache TTL the widget would visibly vanish
    // for the length of a screen capture and read as "taskbar covers it".
    let live = window_rect(self_hwnd);
    for o in occupants.iter_mut() {
        let key = occupant_key(o);
        let now = Instant::now();
        let fresh = content_cache()
            .lock()
            .ok()
            .and_then(|c| c.get(&key).copied())
            .filter(|e| now.duration_since(e.seen) < CONTENT_CACHE_TTL)
            .and_then(|e| e.rect);
        if let Some(c) = fresh {
            o.content = Some(c);
            continue;
        }
        let overlaps_self = live.map(|r| rects_overlap(&r, &o.rect)).unwrap_or(false);
        let was_visible = overlaps_self && unsafe { IsWindowVisible(self_hwnd).as_bool() };
        if was_visible {
            unsafe {
                let _ = ShowWindow(self_hwnd, SW_HIDE);
            }
        }
        let sampled = visible_content_rect(&o.rect);
        if was_visible {
            unsafe {
                let _ = ShowWindow(self_hwnd, SW_SHOWNA);
            }
        }
        o.content = sampled;
        if let Ok(mut c) = content_cache().lock() {
            c.insert(key, ContentEntry { rect: sampled, seen: now });
        }
    }
}

/// Cached tight-content rect per occupant (see `shrink_to_content`).
#[derive(Debug, Clone, Copy)]
struct ContentEntry {
    rect: Option<RECT>,
    seen: Instant,
}

fn content_cache() -> &'static Mutex<HashMap<OccupantKey, ContentEntry>> {
    use std::sync::OnceLock;
    static CACHE: OnceLock<Mutex<HashMap<OccupantKey, ContentEntry>>> = OnceLock::new();
    CACHE.get_or_init(|| Mutex::new(HashMap::new()))
}

/// How long a sampled content rect stays valid before re-sampling.
const CONTENT_CACHE_TTL: Duration = Duration::from_secs(5);

/// Scan `rect` (screen coords) for columns differing from the taskbar
/// background; return the tight bounding box of differing pixels.
/// Returns `None` when sampling fails or nothing differs (fully
/// transparent — caller keeps the full rect as a safe fallback... in fact
/// a fully-transparent window blocks nothing, but treating it as blocking
/// is the conservative choice and such windows are rare).
fn visible_content_rect(rect: &RECT) -> Option<RECT> {
    use windows::Win32::Graphics::Gdi::*;
    unsafe {
        let w = rect.right - rect.left;
        let h = rect.bottom - rect.top;
        if w <= 0 || h <= 0 || w > 4096 || h > 256 {
            return None;
        }
        let hdc_screen = GetDC(None);
        if hdc_screen.is_invalid() {
            return None;
        }
        let hdc_mem = CreateCompatibleDC(hdc_screen);
        if hdc_mem.is_invalid() {
            let _ = ReleaseDC(None, hdc_screen);
            return None;
        }
        let hbmp = CreateCompatibleBitmap(hdc_screen, w, h);
        if hbmp.is_invalid() {
            let _ = DeleteDC(hdc_mem);
            let _ = ReleaseDC(None, hdc_screen);
            return None;
        }
        let old = SelectObject(hdc_mem, hbmp);
        // CAPTUREBLT: also capture layered windows above us so the sample
        // reflects what the user actually sees.
        let ok = BitBlt(
            hdc_mem,
            0,
            0,
            w,
            h,
            hdc_screen,
            rect.left,
            rect.top,
            SRCCOPY | CAPTUREBLT,
        );
        let mut bits = vec![0u32; (w * h) as usize];
        let mut bmi = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
                biWidth: w,
                biHeight: -h, // top-down
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB.0,
                biSizeImage: 0,
                biXPelsPerMeter: 0,
                biYPelsPerMeter: 0,
                biClrUsed: 0,
                biClrImportant: 0,
            },
            ..Default::default()
        };
        let rows = GetDIBits(
            hdc_mem,
            hbmp,
            0,
            h as u32,
            Some(bits.as_mut_ptr() as *mut _),
            &mut bmi,
            DIB_RGB_COLORS,
        );
        let _ = SelectObject(hdc_mem, old);
        let _ = DeleteObject(hbmp);
        let _ = DeleteDC(hdc_mem);
        let _ = ReleaseDC(None, hdc_screen);
        if ok.is_err() || rows == 0 {
            return None;
        }
        // Background estimate: median-ish sample from the rect corners
        // (transparent padding usually touches the edges). Use the four
        // corner pixels + edge midpoints; the most common wins.
        let at = |x: i32, y: i32| -> u32 { bits[(y * w + x) as usize] & 0x00FF_FFFF };
        let mut votes = [at(0, 0), at(w - 1, 0), at(0, h - 1), at(w - 1, h - 1)];
        votes.sort_unstable();
        let bg = votes[1];
        let differs = |c: u32| {
            let dr = ((c & 0xFF) as i32 - (bg & 0xFF) as i32).abs();
            let dg = (((c >> 8) & 0xFF) as i32 - ((bg >> 8) & 0xFF) as i32).abs();
            let db = (((c >> 16) & 0xFF) as i32 - ((bg >> 16) & 0xFF) as i32).abs();
            dr + dg + db > 60
        };
        let (mut lo, mut hi) = (w, -1);
        for x in 0..w {
            let mut col_hit = false;
            let mut y = 0;
            while y < h {
                if differs(at(x, y)) {
                    col_hit = true;
                    break;
                }
                y += 2; // stride 2 rows: 2x faster, still catches any glyph
            }
            if col_hit {
                if x < lo {
                    lo = x;
                }
                hi = x;
            }
        }
        if hi < lo {
            return None;
        }
        // 2px safety margin so antialiased edges never touch us.
        lo = (lo - 2).max(0);
        hi = (hi + 2).min(w - 1);
        Some(RECT {
            left: rect.left + lo,
            top: rect.top,
            right: rect.left + hi + 1,
            bottom: rect.bottom,
        })
    }
}

/// All visible non-OS children of `parent` (potential competing hook widgets).
///
/// Each occupant is annotated with:
/// - `moved` (sticky): ever observed drifting while continuously visible →
///   it has its own avoidance logic and gets out of the way by itself.
/// - `is_new`: first sighting — it may still be running its own avoidance
///   pass, so callers grant it a grace period before yielding.
/// - `blocks_us`: it actually overlaps our live rect (needs our rect, so the
///   caller fills this in via [`mark_blockers`] after placement knows `self`).
pub fn find_occupants(parent: HWND, self_hwnd: HWND) -> Vec<Occupant> {
    let mut ctx = EnumCtx {
        parent,
        self_hwnd: self_hwnd.0,
        out: Vec::new(),
    };
    unsafe {
        let _ = EnumChildWindows(parent, Some(enum_proc), LPARAM(&mut ctx as *mut _ as isize));
    }
    annotate_moves(&mut ctx.out);
    shrink_to_content(&mut ctx.out, self_hwnd);
    ctx.out.sort_by_key(|o| block_rect(o).right);
    ctx.out
}

/// Compare current occupant rects against memory; flag movers and update memory.
///
/// - `is_new`: first sighting this run (or after TTL expiry). A newcomer may
///   still be running its own avoidance pass, so callers give it a grace
///   period before yielding to it.
/// - `moved`: sticky — once an occupant is observed drifting while
///   continuously visible, it is known to have avoidance logic. A single
///   stationary tick must NOT clear the flag, otherwise a widget that moved
///   into place and then stopped would look "static" again.
fn annotate_moves(occupants: &mut [Occupant]) {
    let now = Instant::now();
    let Ok(mut mem) = occupant_memories().lock() else {
        return;
    };
    // Forget stale entries so a long-gone window is treated as new if it
    // reappears later.
    mem.retain(|_, m| now.duration_since(m.seen) < OCCUPANT_MEMORY_TTL);
    for o in occupants.iter_mut() {
        let key = occupant_key(o);
        match mem.get(&key) {
            Some(prev) => {
                o.is_new = false;
                let dx = (o.rect.left - prev.left)
                    .abs()
                    .max((o.rect.right - prev.right).abs());
                let dy = (o.rect.top - prev.top)
                    .abs()
                    .max((o.rect.bottom - prev.bottom).abs());
                if dx > OCCUPANT_MOVE_EPS || dy > OCCUPANT_MOVE_EPS {
                    o.moved = true;
                } else {
                    o.moved = prev.ever_moved;
                }
            }
            None => {
                o.is_new = true;
                o.moved = false;
            }
        }
        mem.insert(
            key,
            OccupantMemory {
                left: o.rect.left,
                top: o.rect.top,
                right: o.rect.right,
                bottom: o.rect.bottom,
                seen: now,
                ever_moved: o.moved,
            },
        );
    }
}

/// Screen-coord rect overlap test (shared by blocker marking and hold checks).
fn rects_overlap(a: &RECT, b: &RECT) -> bool {
    a.left < b.right && a.right > b.left && a.top < b.bottom && a.bottom > b.top
}

/// Fill `blocks_us` for occupants overlapping our live rect (`self_rect` in
/// screen coords). Overlap is tested against the tight content rect (see
/// `shrink_to_content`), NOT the full window rect — transparent padding
/// never blocks. Returns the subset that actually collides.
pub fn mark_blockers(occupants: &mut [Occupant], self_rect: &RECT) -> Vec<Occupant> {
    let mut blockers = Vec::new();
    for o in occupants.iter_mut() {
        let overlap = rects_overlap(self_rect, &block_rect(o));
        o.blocks_us = overlap;
        if overlap {
            blockers.push(o.clone());
        }
    }
    blockers
}

/// Grace period before we yield to a colliding occupant.
///
/// The other widget may have its own avoidance logic running on its own
/// timer: if it just appeared on top of us, it will likely move away on its
/// next tick. Yielding instantly would fling us far for nothing and start a
/// leapfrog chase. Newcomers and known movers (they avoid) get a longer
/// grace than static ones.
const GRACE_STATIC_OCCUPANT: Duration = Duration::from_secs(1);
const GRACE_MOVING_OCCUPANT: Duration = Duration::from_secs(3);
const GRACE_NEW_OCCUPANT: Duration = Duration::from_secs(3);

/// Maximum avoidance displacement per placement pass, measured from our
/// LIVE position.
///
/// If yielding to occupants would move us farther than this from where we
/// already are, we stay put and accept the overlap instead. A very wide
/// occupant (e.g. an 800px lyrics bar) would otherwise fling a 144px widget
/// ~1000px across the taskbar for a few px of edge overlap; holding position
/// is less disruptive. First run has no live rect, so the cap does NOT
/// apply there (there is nothing sensible to hold onto yet). The Win11
/// widgets board is NOT subject to this cap (it is OS chrome and is
/// enforced via min_x/max_right instead).
pub const MAX_AVOID_JUMP_PX: i32 = 500;

#[derive(Debug, Clone, PartialEq, Eq)]
struct HoldParams {
    side: String,
    offset_left: i32,
    offset_top: i32,
    w: i32,
    h: i32,
    band: (i32, i32, i32, i32),
}

#[derive(Debug, Default)]
struct HoldState {
    params: Option<HoldParams>,
    /// Start of the current collision + colliding occupant keys.
    since: Option<Instant>,
    keys: Vec<OccupantKey>,
}

fn hold_state() -> &'static Mutex<HoldState> {
    use std::sync::OnceLock;
    static HOLD: OnceLock<Mutex<HoldState>> = OnceLock::new();
    HOLD.get_or_init(|| Mutex::new(HoldState::default()))
}

/// Forget the hold state machine: the next placement re-anchors instead of
/// defending a stale live rect. Called when the taskbar is rebuilt
/// (explorer restart → `TaskbarCreated`) — the old hold position no longer
/// exists.
pub fn reset_hold_state() {
    if let Ok(mut st) = hold_state().lock() {
        st.params = None;
        st.since = None;
        st.keys.clear();
    }
}

/// `true` once the hold state machine has completed at least one anchored
/// tick with a live rect actually ON the taskbar band. Guards the jump cap:
/// before this, the window may still sit at its 0,0 creation rect and must
/// not be treated as a position worth defending.
fn hold_usable(live: &RECT, band: &RECT, w: i32, h: i32) -> bool {
    if !hold_armed() {
        return false;
    }
    if live.right - live.left != w || live.bottom - live.top != h {
        return false;
    }
    rects_overlap(live, band)
}

/// `true` once the hold state machine has completed at least one anchored
/// tick (i.e. `try_hold_position` ran with settled params). Guards the jump
/// cap: before this, the window may still sit at its 0,0 creation rect and
/// must not be treated as a position worth defending.
fn hold_armed() -> bool {
    hold_state()
        .lock()
        .map(|st| st.params.is_some())
        .unwrap_or(false)
}

/// Decide whether to keep our live position instead of re-anchoring.
///
/// Returns `Some((x_rel, waiting))` to hold, `None` to re-anchor:
/// - first run / live size mismatch / off-band / config or band changed →
///   re-anchor (and remember the new params for next tick).
/// - live collision-free and inside the anchor bounds → hold. Whoever arrived
///   later already placed itself around us, so staying put is correct even if
///   the ideal anchor now sits inside the occupant (the "we were here first,
///   A avoids us, we stay right of A" case).
/// - live colliding but inside the grace period → hold with `waiting=true`,
///   giving the occupant's own avoidance a chance to clear it first.
/// - live colliding past the grace period → re-anchor (we yield).
fn try_hold_position(
    band: &RECT,
    live: &RECT,
    w: i32,
    h: i32,
    side: &str,
    offset_left: i32,
    offset_top: i32,
    min_x: i32,
    max_right: i32,
    live_blockers: &[Occupant],
) -> Option<(i32, bool)> {
    let Ok(mut st) = hold_state().lock() else {
        return None;
    };
    let cur = HoldParams {
        side: side.to_owned(),
        offset_left,
        offset_top,
        w,
        h,
        band: (band.left, band.top, band.right, band.bottom),
    };
    // New config / band / first sighting: anchor this tick, hold from next.
    // NOTE: this must return None (re-anchor) — NOT Some(live_x). Returning
    // the live position here would freeze us at the 0,0 creation rect (or
    // wherever the window happens to be) and the jump cap would then defend
    // that bogus spot forever.
    if st.params.as_ref() != Some(&cur) {
        st.params = Some(cur);
        st.since = None;
        st.keys.clear();
        return None;
    }
    // Live rect not ours yet (being resized) or not on the band: anchor.
    if live.right - live.left != w || live.bottom - live.top != h {
        st.since = None;
        st.keys.clear();
        return None;
    }
    if !rects_overlap(live, band) {
        st.since = None;
        st.keys.clear();
        return None;
    }
    let live_x = live.left - band.left;
    // Tray icons expanded over us (right) or the reserved zone grew under us
    // (left): our spot is gone regardless of occupants — re-anchor.
    if live_x < min_x || live_x + w > max_right {
        st.since = None;
        st.keys.clear();
        return None;
    }
    if live_blockers.is_empty() {
        st.since = None;
        st.keys.clear();
        return Some((live_x, false));
    }
    let mut key: Vec<OccupantKey> = live_blockers.iter().map(occupant_key).collect();
    key.sort_by(|a, b| (&a.pid, &a.class, a.h_bucket).cmp(&(&b.pid, &b.class, b.h_bucket)));
    let grace = if live_blockers.iter().any(|b| b.moved || b.is_new) {
        GRACE_MOVING_OCCUPANT.max(GRACE_NEW_OCCUPANT)
    } else {
        GRACE_STATIC_OCCUPANT
    };
    match st.since {
        Some(t) if st.keys == key && t.elapsed() < grace => Some((live_x, true)),
        _ => {
            if st.keys != key || st.since.is_none() {
                // New collision: start the grace clock, hold this tick.
                st.since = Some(Instant::now());
                st.keys = key;
                Some((live_x, true))
            } else {
                // Same collision outlasted the grace: we yield.
                st.since = None;
                st.keys.clear();
                None
            }
        }
    }
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
    pub occupants: Vec<Occupant>,
    /// Occupants that actually collide with our live rect (the ones we yield to).
    pub blockers: Vec<Occupant>,
    /// `true` when we already sit collision-free and hold position.
    pub held: bool,
    /// `true` when the jump cap fired: we stayed instead of yielding far.
    /// `blockers` still lists whoever we overlap (for logging).
    pub capped: bool,
}

/// Compute widget placement inside the taskbar band.
///
/// - `w/h`: desired widget size (physical px, already DPI-scaled by caller).
/// - Right side (default): `x = notify.left - w + 2`, shifted left past occupants.
/// - Left side: anchored after `Start`, shifted right past occupants.
/// - `avoid_overlap` switch: when `false`, pin to the raw anchor for
///   third-party occupants only (no shifting, no hold). The Win11
///   widgets board is ALWAYS avoided regardless of this flag.
/// - Jump cap (`MAX_AVOID_JUMP_PX`): a yield that would displace us farther
///   than this from our live position (or raw anchor on first run) is
///   refused — we stay and accept the overlap. Prevents a wide occupant
///   from catapulting us across the taskbar.
///
/// Anti-leapfrog rule: when `self_hwnd` already has a live rect on the
/// taskbar and that rect does NOT overlap any occupant, the occupant must
/// have placed itself around us (it has its own avoidance logic) — so we
/// hold our current position instead of re-anchoring and jumping left.
/// Only when our live rect actually collides do we yield.
pub fn compute_placement(
    tray: HWND,
    self_hwnd: HWND,
    w: i32,
    h: i32,
    side: &str,
    offset_left: i32,
    offset_top: i32,
    left_space_win11: i32,
    right_space_fallback: i32,
    avoid_widgets: bool,
    avoid_overlap: bool,
    first: bool,
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

            let mut occupants = find_occupants(tray, self_hwnd);
            // Switch off: ignore third-party occupants only. `occupants` is
            // still returned for logging, but nothing may move us. The
            // widgets board below is ALWAYS respected regardless of the flag.
            if !avoid_overlap {
                occupants.clear();
            }
            // The weather/widgets board is NOT an HWND occupant — it is XAML
            // content inside the bridge, so `EnumChildWindows` never sees it.
            // Its UIA rect is the ground truth; fall back to the registry
            // estimate only when UIA is unavailable.
            //
            // The board sits just LEFT of TrayNotifyWnd (right side of the
            // taskbar when TaskbarAl=0), NOT at x=0: UIA on this machine
            // reports WidgetsButton=(2007,1552,2159,1600) while
            // TrayNotifyWnd starts at 2171. So the reserved zone is
            // [board_left, notify_left): right-anchored widgets must stop at
            // board_left, left-anchored ones must start at notify_left... in
            // practice: right side keeps clear of the board, left side keeps
            // the legacy x=0 estimate.
            //
            // NOTE: this reserve is UNCONDITIONAL — it applies even when the
            // `avoid_overlap` switch is off. The board is OS chrome, not a
            // third-party widget; overlapping it is never acceptable.
            let board = widgets_button_rect();
            let board_left = board.map(|r| r.left - bar.left);
            let board_right = board.map(|r| r.right - bar.left);
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
            let _ = board_right;

            // Hold check: if our live rect is already collision-free, the
            // occupant placed itself around us (it has avoidance logic) — we
            // stay put even when the ideal anchor now sits inside it.
            //
            // Band pre-filter: occupants fully outside [min_x, max_right] can
            // never be hit by a legal position, so they must not veto a hold
            // (e.g. a wide widget whose tail sticks past the notify edge
            // while our anchor sits in the free gap). Filter BEFORE the hold
            // check so a stale tail can't force a re-anchor either.
            occupants.retain(|o| {
                let br = block_rect(o);
                let ol = br.left - bar.left;
                let or = br.right - bar.left;
                or > min_x && ol < max_right
            });
            let (mut held, mut waiting) = (false, false);
            let mut x = if !avoid_overlap {
                // Pinned: raw anchor, no shifting, no hold.
                anchor_x_win11(
                    side,
                    max_right - 2,
                    min_x,
                    w,
                    &[],
                    &bar,
                    &mut Vec::new(),
                )
            } else if let Some(live) = window_rect(self_hwnd) {
                let live_blockers = mark_blockers(&mut occupants, &live);
                if let Some((hx, w8)) = try_hold_position(
                    &bar,
                    &live,
                    w,
                    h,
                    side,
                    offset_left,
                    offset_top,
                    min_x,
                    max_right,
                    &live_blockers,
                ) {
                    held = true;
                    waiting = w8;
                    // try_hold_position returns the live x WITHOUT the user
                    // offset (it compares against un-offset bounds); the
                    // single `x += offset_left` below applies it exactly once.
                    hx
                } else {
                    anchor_x_win11(
                        side,
                        max_right - 2,
                        min_x,
                        w,
                        &occupants,
                        &bar,
                        &mut Vec::new(),
                    )
                }
            } else {
                anchor_x_win11(
                    side,
                    max_right - 2,
                    min_x,
                    w,
                    &occupants,
                    &bar,
                    &mut Vec::new(),
                )
            };
            x += offset_left;
            // The board reserve is a hard floor for LEFT-anchored widgets
            // even when the switch is off: clamp() alone could push us back
            // onto the weather board when the band is crowded, so re-assert
            // min_x afterwards.
            if side == "left" {
                x = x.max(min_x);
            }
            x = x.clamp(2, (bar_w - w - 2).max(2));

            // Jump cap (see MAX_AVOID_JUMP_PX): refuse far yields, stay put.
            // First call (first=true, window still at 0,0) bypasses it via
            // hold_usable(): there is no position worth defending yet.
            let mut capped = false;
            if avoid_overlap && !first {
                if let Some(live) = window_rect(self_hwnd) {
                    if hold_usable(&live, &bar, w, h) {
                        let reference = live.left - bar.left;
                        if (x - reference).abs() > MAX_AVOID_JUMP_PX {
                            x = reference.clamp(2, (bar_w - w - 2).max(2));
                            held = true;
                            waiting = false;
                            capped = true;
                        }
                    }
                }
            }

            let blockers = if held && !waiting && !capped {
                Vec::new()
            } else if let Some(live) = window_rect(self_hwnd) {
                let mut live_rect = live;
                live_rect.left = bar.left + x;
                live_rect.right = live_rect.left + w;
                mark_blockers(&mut occupants, &live_rect)
            } else {
                Vec::new()
            };

            Some(Placement {
                parent: tray,
                x,
                y,
                w,
                h,
                kind,
                occupants,
                blockers,
                held,
                capped,
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
            let mut occupants = find_occupants(rebar, self_hwnd);
            if !avoid_overlap {
                occupants.clear();
            }
            let (mut held, mut waiting) = (false, false);
            let mut x = if !avoid_overlap {
                anchor_x_classic(side, band_w, w, &[], &band)
            } else if let Some(live) = window_rect(self_hwnd) {
                let live_blockers = mark_blockers(&mut occupants, &live);
                if let Some((hx, w8)) = try_hold_position(
                    &band,
                    &live,
                    w,
                    h,
                    side,
                    offset_left,
                    offset_top,
                    2,
                    band_w - 2,
                    &live_blockers,
                ) {
                    held = true;
                    waiting = w8;
                    hx
                } else {
                    anchor_x_classic(side, band_w, w, &occupants, &band)
                }
            } else {
                anchor_x_classic(side, band_w, w, &occupants, &band)
            };
            x += offset_left;
            x = x.clamp(2, (band_w - w - 2).max(2));
            // Jump cap (see MAX_AVOID_JUMP_PX): refuse far yields, stay put.
            // First call exempt — see Win11 branch.
            let mut capped = false;
            if avoid_overlap && !first {
                if let Some(live) = window_rect(self_hwnd) {
                    if hold_usable(&live, &band, w, h) {
                        let reference = live.left - band.left;
                        if (x - reference).abs() > MAX_AVOID_JUMP_PX {
                            x = reference.clamp(2, (band_w - w - 2).max(2));
                            held = true;
                            waiting = false;
                            capped = true;
                        }
                    }
                }
            }
            let y = (band_h - h) / 2 + offset_top;
            let blockers = if held && !waiting && !capped {
                Vec::new()
            } else if let Some(live) = window_rect(self_hwnd) {
                let mut live_rect = live;
                live_rect.left = band.left + x;
                live_rect.right = live_rect.left + w;
                mark_blockers(&mut occupants, &live_rect)
            } else {
                Vec::new()
            };
            Some(Placement {
                parent: rebar,
                x,
                y,
                w,
                h,
                kind,
                occupants,
                blockers,
                held,
                capped,
            })
        }
    }
}

/// Ideal anchor + occupant shift for Win11 (no hold logic).
///
/// `blockers_out` receives the occupants the candidate actually overlaps
/// (for logging); pass a throwaway `&mut Vec::new()` when not needed.
fn anchor_x_win11(
    side: &str,
    notify_left: i32,
    min_x: i32,
    w: i32,
    occupants: &[Occupant],
    bar: &RECT,
    blockers_out: &mut Vec<Occupant>,
) -> i32 {
    if side == "left" {
        let mut lx = min_x;
        for o in occupants {
            // Content rect: transparent padding never blocks (see block_rect).
            let br = block_rect(o);
            let ol = br.left - bar.left;
            let or = br.right - bar.left;
            if lx < or && lx + w > ol {
                lx = or + 2;
                blockers_out.push(o.clone());
            }
        }
        lx
    } else {
        // Right anchor stops at the widgets board (passed in as
        // `notify_left` = min(board_left, tray_notify_left)); the board is
        // XAML content the HWND enumeration never sees, so the anchor edge
        // is the ONLY thing keeping us off it.
        let mut rx = notify_left - w + 2;
        for o in occupants.iter().rev() {
            // Content rect: transparent padding never blocks (see block_rect).
            let br = block_rect(o);
            let ol = br.left - bar.left;
            let or = br.right - bar.left;
            if rx < or && rx + w > ol {
                rx = ol - w - 2;
                blockers_out.push(o.clone());
            }
        }
        rx
    }
}

/// Ideal anchor + occupant shift for Classic bands.
fn anchor_x_classic(side: &str, band_w: i32, w: i32, occupants: &[Occupant], band: &RECT) -> i32 {
    let mut x = if side == "left" { 2 } else { band_w - w - 2 };
    if side == "left" {
        for o in occupants {
            let br = block_rect(o);
            let ol = br.left - band.left;
            let or = br.right - band.left;
            if x < or && x + w > ol {
                x = or + 2;
            }
        }
    } else {
        for o in occupants.iter().rev() {
            let br = block_rect(o);
            let ol = br.left - band.left;
            let or = br.right - band.left;
            if x < or && x + w > ol {
                x = ol - w - 2;
            }
        }
    }
    x
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
/// The UIA walk is expensive (CoCreateInstance + bounded 400-node DFS) and
/// the button only moves when the notify/tray geometry moves, so results are
/// cached for `WIDGETS_CACHE_TTL` or until the notify edge shifts. When the
/// board is disabled (registry `TaskbarDa=0`) the walk is skipped entirely.
pub fn widgets_button_rect() -> Option<RECT> {
    // Registry first: with the board off, UIA would report nothing — skip
    // the walk (registry flips are picked up live on the next placement).
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

/// Uncached UIA walk for the `WidgetsButton` rect (see `widgets_button_rect`).
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
        // TaskbarFrame -> WidgetsButton (depth-first, bounded).
        let walker = uia.ControlViewWalker().ok()?;
        let mut stack = vec![root];
        let mut guard = 0;
        while let Some(el) = stack.pop() {
            guard += 1;
            if guard > 400 {
                break;
            }
            if let Ok(aid) = el.CurrentAutomationId() {
                if aid == "WidgetsButton" {
                    if let Ok(r) = el.CurrentBoundingRectangle() {
                        return Some(RECT {
                            left: r.left,
                            top: r.top,
                            right: r.right,
                            bottom: r.bottom,
                        });
                    }
                    return None;
                }
            }
            let mut child = walker.GetFirstChildElement(&el).ok();
            let mut n = 0;
            while let Some(c) = child {
                if n >= 30 {
                    break;
                }
                stack.push(c.clone());
                child = walker.GetNextSiblingElement(&c).ok();
                n += 1;
            }
        }
        None
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

/// Cheap change detector for avoidance: a hash of the visible
/// non-whitelisted taskbar children's rects. Plain occupant moves fire no
/// UIA structure event, so this is how the UI thread notices them between
/// placement passes (a few syscalls — safe on a fast poll).
pub fn occupancy_fingerprint(parent: HWND, self_hwnd: HWND) -> u64 {
    struct Ctx {
        self_hwnd: HWND,
        rects: Vec<(i32, i32, i32, i32)>,
    }
    unsafe extern "system" fn collect(hwnd: HWND, lparam: LPARAM) -> BOOL {
        let ctx = &mut *(lparam.0 as *mut Ctx);
        if hwnd == ctx.self_hwnd {
            return true.into();
        }
        if !is_window_visible(hwnd) {
            return true.into();
        }
        let class = class_name(hwnd);
        if WHITELIST.iter().any(|w| *w == class) {
            return true.into();
        }
        if class == "RazerTaskbarWidget" || pid_of(hwnd) == own_pid() {
            return true.into();
        }
        if let Some(r) = window_rect(hwnd) {
            ctx.rects.push((r.left, r.top, r.right, r.bottom));
        }
        true.into()
    }

    let mut ctx = Ctx { self_hwnd, rects: Vec::new() };
    unsafe {
        let _ = EnumChildWindows(parent, Some(collect), LPARAM(&mut ctx as *mut _ as isize));
    }
    // FNV-1a over the sorted-independent rect list.
    let mut hash: u64 = 0xcbf29ce484222325;
    for (x, y, r, b) in &ctx.rects {
        for v in [*x, *y, *r, *b] {
            hash ^= v as u64;
            hash = hash.wrapping_mul(0x100000001b3);
        }
    }
    hash ^ (ctx.rects.len() as u64).wrapping_mul(0x9E37_79B9_7F4A_7C15)
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
