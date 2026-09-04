//! Razer battery widget hooked into the Windows taskbar.
//!
//! Port of the Electron `src/watcher/*` log parsing logic:
//! - V3: `_OnBatteryLevelChanged` / `_OnDeviceLoaded` / `_OnDeviceRemoved`
//! - V4: `connectingDeviceData: {...}` JSON (last line wins for connection state)
//! - Display pick: user-selected handle first, else lowest battery preferring non-charging.
//!
//! Window embedding is a Taskbar-Lyrics style top-level overlay:
//! - `WS_POPUP | WS_EX_LAYERED` + `HWND_TOPMOST`, never a `WS_CHILD` (a child
//!   of the taskbar is composited under TranslucentTB-class acrylic effects).
//! - Layout is event-driven: the `TaskbarCreated` broadcast (explorer restart)
//!   and UIA structure-change events trigger a coalesced placement pass; a 1s
//!   timer stays as the fallback poll (`window.rs` / `uia_events.rs`).
//! - Coexistence: enumerate taskbar children, skip the OS whitelist, and
//!   yield/hold per the hold/grace/jump-cap state machine (`taskbar.rs`).

mod battery;
mod config;
mod history;
mod hover;
mod i18n;
mod icons;
mod taskbar;
mod tray;
mod uia_events;
mod viewer;
mod watcher;
mod window;

use std::sync::{Arc, Mutex};
use std::time::Duration;

use watcher::{DeviceMap, RazerWatcher};

fn main() {
    // STA COM for the UI thread: UIA queries in taskbar.rs need it.
    // OleInitialize would also work; CoInitializeEx(STA) is the minimal bit.
    unsafe {
        let _ = windows::Win32::System::Com::CoInitializeEx(
            None,
            windows::Win32::System::Com::COINIT_APARTMENTTHREADED,
        );
    }
    let cfg = config::load();
    let poll = Duration::from_secs(cfg.polling_throttle_secs.max(2));

    let devices: Arc<Mutex<DeviceMap>> = Arc::new(Mutex::new(DeviceMap::new()));

    // Battery history DB (recording can be switched off in settings; a
    // failure here just disables the feature, never the widget).
    history::init();
    // UI language from settings (or the system UI language for "auto").
    i18n::init();

    // Single instance: exit if a previous widget window already exists.
    if window::find_existing_instance() {
        eprintln!("razer-taskbar: another instance is already running");
        return;
    }

    // Spawn the log watcher thread (V3/V4 auto-detect, notify + poll fallback).
    // The watcher re-reads settings.json every cycle, so no config is passed.
    let watcher_devices = Arc::clone(&devices);
    std::thread::Builder::new()
        .name("razer-watcher".into())
        .spawn(move || {
            let watcher = RazerWatcher::new(watcher_devices);
            watcher.run(poll);
        })
        .expect("spawn watcher thread");

    // Message loop + taskbar hook runs on the main thread.
    window::run_message_loop(devices, cfg);
}
