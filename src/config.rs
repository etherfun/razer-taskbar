//! JSON config in `%APPDATA%/razer-taskbar/settings.json` + Run-key autostart.
//! Replaces `settings_manager.ts` (no Electron IPC; menu edits rewrite the file).

use std::fs;
use std::path::PathBuf;

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct Config {
    pub run_at_startup: bool,
    pub polling_throttle_secs: u64,
    pub shown_device_handle: String,
    /// "auto" | "v3" | "v4"
    pub synapse_version: String,
    /// "left" | "right" — widget side relative to TrayNotifyWnd
    pub widget_side: String,
    pub avoid_overlap_with_widgets: bool,
    /// Reserved space after Start for the (XAML-invisible) taskbar app
    /// icons on Win11.
    pub taskbar_left_space_win11: i32,
    pub taskbar_right_space_win11: i32,
    pub window_offset_left: i32,
    pub window_offset_top: i32,
    pub show_tray_icon: bool,
    /// Hover popover on the widget listing every device (see `hover.rs`).
    pub hover_devices: bool,
    /// Record battery samples to battery.db and predict usage time (`history.rs`).
    pub record_battery_history: bool,
    /// Show the predicted remaining time as the widget's second row
    /// (`history.rs`; the widget keeps its width — the prediction stacks
    /// below the percentage instead of widening it).
    pub show_estimated_time: bool,
    /// Watcher cadence while recording is enabled — finer than the display
    /// poll so connect/charge/level transitions are timestamped precisely.
    pub history_poll_interval_secs: u64,
    /// UI language: "auto" (follow Windows) | "en" | "zh" (`i18n.rs`).
    pub language: String,
    /// Experimental: parent the widget into the taskbar band as a WS_CHILD
    /// (Lyricify's taskbar-lyrics trick) instead of a topmost overlay. Rides
    /// every shell raise for free; unsupported and update-fragile, so the
    /// overlay stays the default and any SetParent rejection falls back.
    pub embed_into_taskbar: bool,
}

impl Default for Config {
    fn default() -> Self {
        Self {
            run_at_startup: false,
            polling_throttle_secs: 15,
            shown_device_handle: String::new(),
            synapse_version: "auto".into(),
            widget_side: "right".into(),
            avoid_overlap_with_widgets: true,
            taskbar_left_space_win11: 160,
            taskbar_right_space_win11: 88,
            window_offset_left: 0,
            window_offset_top: 0,
            show_tray_icon: true,
            hover_devices: true,
            record_battery_history: true,
            show_estimated_time: true,
            history_poll_interval_secs: 5,
            language: "auto".into(),
            embed_into_taskbar: false,
        }
    }
}

pub fn config_path() -> PathBuf {
    let base = std::env::var_os("APPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."));
    base.join("razer-taskbar").join("settings.json")
}

/// Battery history database (see `history.rs`).
pub fn db_path() -> PathBuf {
    let base = std::env::var_os("APPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."));
    base.join("razer-taskbar").join("battery.db")
}

pub fn load() -> Config {
    let path = config_path();
    let Ok(text) = fs::read_to_string(&path) else {
        return Config::default();
    };
    let mut cfg: Config = serde_json::from_str(&text).unwrap_or_default();
    // Merge-with-defaults: serde(default) already fills missing keys.
    if cfg.polling_throttle_secs == 0 {
        cfg.polling_throttle_secs = 15;
    }
    if cfg.history_poll_interval_secs == 0 {
        cfg.history_poll_interval_secs = 5;
    }
    cfg
}

pub fn save(cfg: &Config) {
    let path = config_path();
    if let Some(parent) = path.parent() {
        let _ = fs::create_dir_all(parent);
    }
    if let Ok(text) = serde_json::to_string_pretty(cfg) {
        let _ = fs::write(path, text);
    }
}

/// Sync HKCU `...\\Run\\RazerTaskbar` with `run_at_startup`.
pub fn apply_autostart(enabled: bool) {
    use windows::core::w;
    use windows::Win32::System::Registry::{
        RegCloseKey, RegOpenKeyExW, RegSetValueExW, HKEY_CURRENT_USER, KEY_SET_VALUE, REG_SZ,
    };

    let exe = std::env::current_exe()
        .map(|p| p.to_string_lossy().into_owned())
        .unwrap_or_default();
    if exe.is_empty() {
        return;
    }
    unsafe {
        let mut key = Default::default();
        if RegOpenKeyExW(
            HKEY_CURRENT_USER,
            w!("Software\\Microsoft\\Windows\\CurrentVersion\\Run"),
            0,
            KEY_SET_VALUE,
            &mut key,
        )
        .is_err()
        {
            return;
        }
        if enabled {
            let value: Vec<u16> = exe.encode_utf16().chain(std::iter::once(0)).collect();
            let bytes =
                std::slice::from_raw_parts(value.as_ptr() as *const u8, value.len() * 2);
            let _ = RegSetValueExW(key, w!("RazerTaskbar"), 0, REG_SZ, Some(bytes));
        } else {
            let _ = windows::Win32::System::Registry::RegDeleteValueW(key, w!("RazerTaskbar")).ok();
        }
        let _ = RegCloseKey(key);
    }
}
