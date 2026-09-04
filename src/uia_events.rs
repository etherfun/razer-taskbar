//! UIA structure-change listener for event-driven taskbar re-layout
//! (Taskbar-Lyrics `Handler.cppm`/`Taskbar.cppm` port).
//!
//! A background MTA thread registers an
//! `IUIAutomationStructureChangedEventHandler` on the Win11 taskbar's XAML
//! input site (`Windows.UI.Input.InputSite.WindowClass` under
//! `Shell_TrayWnd`, `TreeScope_Descendants`). The handler itself does
//! nothing but `PostMessageW(target, msg)` — no locks, no shared state, so
//! it is safe to call from whatever thread UIA delivers events on. The
//! widget window coalesces the posted messages (see `window.rs`).
//!
//! The thread re-registers when the taskbar HWND changes (explorer restart:
//! either signalled via the returned channel from the `TaskbarCreated`
//! handler, or self-detected by the 30s idle check).

use std::sync::mpsc::{channel, Receiver, RecvTimeoutError, Sender};
use std::time::Duration;

use windows::core::implement;
use windows::Win32::Foundation::{HWND, LPARAM, WPARAM};
use windows::Win32::System::Com::{
    CoCreateInstance, CoInitializeEx, CoUninitialize, CLSCTX_INPROC_SERVER, COINIT_MULTITHREADED,
    SAFEARRAY,
};
use windows::Win32::UI::Accessibility::{
    CUIAutomation, IUIAutomation, IUIAutomationElement, IUIAutomationStructureChangedEventHandler,
    IUIAutomationStructureChangedEventHandler_Impl, StructureChangeType, TreeScope_Descendants,
};
use windows::Win32::UI::WindowsAndMessaging::PostMessageW;

use crate::taskbar::find_shell_tray;

/// Wake the listener thread so it re-checks the taskbar HWND and re-registers.
pub type RebindTx = Sender<()>;

/// Spawn the listener thread. `target` receives `msg` (zero params) on every
/// taskbar structure change. The returned sender should be signalled (send
/// `()`) whenever `TaskbarCreated` is observed.
pub fn spawn(target: HWND, msg: u32) -> RebindTx {
    let (tx, rx) = channel::<()>();
    // HWND is !Send: cross the thread boundary as a bare address.
    let target_addr = target.0 as isize;
    if let Err(e) = std::thread::Builder::new()
        .name("razer-uia-events".into())
        .spawn(move || run_loop(target_addr, msg, rx))
    {
        eprintln!("razer-taskbar: UIA listener thread failed to spawn: {e}");
    }
    tx
}

/// One live registration. Keeping the UIA + element + handler pointers lets
/// the next rebind release the old handler instead of leaking it.
struct Registration {
    tray: HWND,
    uia: IUIAutomation,
    element: IUIAutomationElement,
    handler: IUIAutomationStructureChangedEventHandler,
}

impl Registration {
    fn teardown(self) {
        // A dead taskbar element makes removal fail — harmless, ignore it.
        unsafe {
            let _ = self
                .uia
                .RemoveStructureChangedEventHandler(&self.element, &self.handler);
        }
    }
}

fn run_loop(target_addr: isize, msg: u32, rx: Receiver<()>) {
    let hr = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) };
    if hr.is_err() {
        eprintln!("razer-taskbar: UIA listener CoInitializeEx failed: {hr:?}");
        return;
    }
    let mut reg: Option<Registration> = None;
    loop {
        // Check/rebind FIRST, then wait: the first iteration registers
        // immediately instead of waiting out the 30s idle timeout.
        let tray = find_shell_tray();
        let need_rebind = match (&reg, tray) {
            (None, Some(_)) => true,
            (Some(r), Some(t)) => r.tray.0 != t.0,
            // No taskbar right now: keep waiting; a stale registration
            // on a dead taskbar delivers nothing and costs nothing.
            (Some(_), None) | (None, None) => false,
        };
        if need_rebind {
            if let Some(old) = reg.take() {
                old.teardown();
            }
            if let Some(t) = tray {
                reg = register(t, target_addr, msg);
            }
        }
        // Wake on rebind signal, or at least every 30s to notice the taskbar
        // HWND changing on its own (fallback if TaskbarCreated was missed,
        // and the retry path when explorer is not up yet).
        match rx.recv_timeout(Duration::from_secs(30)) {
            Ok(()) | Err(RecvTimeoutError::Timeout) => {}
            Err(RecvTimeoutError::Disconnected) => break,
        }
    }
    if let Some(old) = reg.take() {
        old.teardown();
    }
    unsafe { CoUninitialize() };
}

fn register(tray: HWND, target_addr: isize, msg: u32) -> Option<Registration> {
    unsafe {
        let uia: IUIAutomation = match CoCreateInstance(&CUIAutomation, None, CLSCTX_INPROC_SERVER)
        {
            Ok(u) => u,
            Err(e) => {
                eprintln!("razer-taskbar: UIA CoCreateInstance failed: {e}");
                return None;
            }
        };
        let root = match uia.ElementFromHandle(tray) {
            Ok(el) => el,
            Err(e) => {
                eprintln!("razer-taskbar: UIA ElementFromHandle failed: {e}");
                return None;
            }
        };
        // Scope to the XAML input site when present (Win11): the taskbar's
        // live content lives under it, and scoping trims event noise from
        // other taskbar children. Fall back to the whole tray (Win10 has no
        // input site; the 1s poll covers it there anyway).
        let element = find_input_site(&uia, &root).unwrap_or_else(|| root.clone());
        let handler: IUIAutomationStructureChangedEventHandler =
            StructureHandler { target: target_addr, msg }.into();
        if let Err(e) = uia.AddStructureChangedEventHandler(
            &element,
            TreeScope_Descendants,
            None,
            &handler,
        ) {
            eprintln!("razer-taskbar: AddStructureChangedEventHandler failed: {e}");
            return None;
        }
        eprintln!("razer-taskbar: UIA structure listener registered");
        Some(Registration {
            tray,
            uia,
            element,
            handler,
        })
    }
}

/// First `Windows.UI.Input.InputSite.WindowClass` descendant (bounded DFS) —
/// the root Taskbar-Lyrics scopes its structure listener to.
fn find_input_site(
    uia: &IUIAutomation,
    root: &IUIAutomationElement,
) -> Option<IUIAutomationElement> {
    const WANT: &str = "Windows.UI.Input.InputSite.WindowClass";
    unsafe {
        let walker = uia.ControlViewWalker().ok()?;
        let mut stack = vec![root.clone()];
        let mut guard = 0;
        while let Some(el) = stack.pop() {
            guard += 1;
            if guard > 200 {
                break;
            }
            if let Ok(name) = el.CurrentClassName() {
                if name == WANT {
                    return Some(el);
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
    }
    None
}

// The COM handler is invoked on arbitrary UIA delivery threads; `target` is
// stored as a bare address (HWND is !Send) and only used for PostMessageW,
// which is thread-safe.
#[implement(IUIAutomationStructureChangedEventHandler)]
struct StructureHandler {
    target: isize,
    msg: u32,
}

impl IUIAutomationStructureChangedEventHandler_Impl for StructureHandler_Impl {
    fn HandleStructureChangedEvent(
        &self,
        _sender: Option<&IUIAutomationElement>,
        _changetype: StructureChangeType,
        _runtimeid: *const SAFEARRAY,
    ) -> windows::core::Result<()> {
        unsafe {
            let _ = PostMessageW(
                HWND(self.target as *mut _),
                self.msg,
                WPARAM(0),
                LPARAM(0),
            );
        }
        Ok(())
    }
}
