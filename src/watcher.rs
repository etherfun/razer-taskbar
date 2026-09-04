//! Synapse log watcher (ported from `watcherV3.ts` / `watcherV4.ts` / `razer_watcher.ts`).
//!
//! - V3: `%LOCALAPPDATA%/Razer/Synapse3/Log/Razer Synapse 3.log`
//!   `_OnBatteryLevelChanged ... Name: X ... Handle: N ... level L state S`
//!   plus `_OnDeviceLoaded` / `_OnDeviceRemoved` for connection state.
//! - V4: `%LOCALAPPDATA%/Razer/RazerAppEngine/User Data/Logs/systray_systrayv2*.log`
//!   `[...] connectingDeviceData: [{...hasBattery...powerStatus...}]` — the whole
//!   history is replayed (`watcherV4.ts` parity); the last snapshot decides
//!   connection state.
//! - `notify` watches the files; a poll loop re-parses every `polling_throttle_secs`
//!   (settings are re-read from disk each pass, so menu edits apply without restart).

use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use notify::{RecursiveMode, Watcher};
use regex::Regex;
use serde::Deserialize;

use crate::battery::RazerDevice;

pub type DeviceMap = HashMap<String, RazerDevice>;

/// Deserialize `T`, treating an explicit JSON `null` as `T::default()`.
///
/// `#[serde(default)]` only covers *missing* fields, but Synapse logs emit
/// explicit nulls (e.g. `"serialNumber": null`). Without this, one null field
/// aborts the whole snapshot parse and the widget stays at `--`.
fn null_to_default<'de, D, T>(deserializer: D) -> Result<T, D::Error>
where
    D: serde::Deserializer<'de>,
    T: Default + serde::Deserialize<'de>,
{
    Ok(Option::<T>::deserialize(deserializer)?.unwrap_or_default())
}

pub struct RazerWatcher {
    devices: Arc<Mutex<DeviceMap>>,
    last_v4_timestamp: Mutex<String>,
}

impl RazerWatcher {
    pub fn new(devices: Arc<Mutex<DeviceMap>>) -> Self {
        Self {
            devices,
            last_v4_timestamp: Mutex::new(String::new()),
        }
    }

    pub fn v3_log_path() -> Option<PathBuf> {
        std::env::var_os("LOCALAPPDATA").map(|base| {
            Path::new(&base)
                .join("Razer")
                .join("Synapse3")
                .join("Log")
                .join("Razer Synapse 3.log")
        })
    }

    pub fn v4_log_dir() -> Option<PathBuf> {
        std::env::var_os("LOCALAPPDATA").map(|base| {
            Path::new(&base)
                .join("Razer")
                .join("RazerAppEngine")
                .join("User Data")
                .join("Logs")
        })
    }

    /// Highest-index `systray_systrayv2*.log` (mirrors `findLatestSynapseV4LogFile`).
    pub fn latest_v4_log(dir: &Path) -> Option<PathBuf> {
        let re = Regex::new(r"^systray_systrayv2(?P<index>\d*)\.log$").ok()?;
        let mut best: Option<(i64, PathBuf)> = None;
        let entries = fs::read_dir(dir).ok()?;
        for entry in entries.flatten() {
            let name = entry.file_name().to_string_lossy().into_owned();
            let Some(caps) = re.captures(&name) else {
                continue;
            };
            let idx: i64 = caps
                .name("index")
                .map(|m| m.as_str().parse().unwrap_or(-1))
                .unwrap_or(-1);
            match &best {
                Some((best_idx, _)) if *best_idx >= idx => {}
                _ => best = Some((idx, entry.path())),
            }
        }
        best.map(|(_, p)| p)
    }

    pub fn run(&self, poll: Duration) {
        // Initial parse so the widget shows something immediately.
        self.parse_once();

        let (tx, rx) = std::sync::mpsc::channel();
        let mut watcher = notify::recommended_watcher(tx).expect("create file watcher");
        if let Some(p) = Self::v3_log_path() {
            if p.exists() {
                let _ = watcher.watch(&p, RecursiveMode::NonRecursive);
            }
        }
        if let Some(dir) = Self::v4_log_dir() {
            if dir.exists() {
                let _ = watcher.watch(&dir, RecursiveMode::NonRecursive);
            }
        }

        let mut interval = poll;
        loop {
            // Drain filesystem events (debounced by the poll interval below).
            while rx.try_recv().is_ok() {}
            // Re-resolve the V4 log in case Synapse rotated to a new file.
            self.parse_once();
            // Battery history sampling rides on this cadence; recording
            // wants a finer tick than the display poll so connect/charge/
            // level transitions land on precise timestamps.
            let cfg = crate::config::load();
            if cfg.record_battery_history {
                crate::history::record(&self.devices);
            }
            std::thread::sleep(interval);
            // TS applies `pollingThrottleSeconds` via watcher restart; this
            // watcher is long-lived, so re-read it (menu edits apply live).
            // While recording, the tighter record interval wins.
            let cfg = crate::config::load();
            interval = if cfg.record_battery_history && crate::history::ready() {
                Duration::from_secs(
                    cfg.polling_throttle_secs
                        .min(cfg.history_poll_interval_secs)
                        .max(1),
                )
            } else {
                Duration::from_secs(cfg.polling_throttle_secs.max(2))
            };
        }
    }

    fn parse_once(&self) {
        // Re-read settings every pass: the copy captured at startup would
        // otherwise clobber menu edits to the shown device.
        match crate::config::load().synapse_version.as_str() {
            "v3" => self.parse_v3(),
            "v4" => self.parse_v4(),
            _ => {
                // auto: V4 wins when its log dir has candidates (mirrors TS `auto`).
                let has_v4 = Self::v4_log_dir()
                    .map(|d| Self::latest_v4_log(&d).is_some())
                    .unwrap_or(false);
                if has_v4 {
                    self.parse_v4();
                } else {
                    self.parse_v3();
                }
            }
        }
    }

    fn parse_v3(&self) {
        let Some(path) = Self::v3_log_path() else {
            return;
        };
        let Ok(log) = fs::read_to_string(&path) else {
            return;
        };
        let battery_re = Regex::new(
            r"(?m)^(?P<dateTime>.+?) INFO.+?_OnBatteryLevelChanged[\s\S]*?Name: (?P<name>.*)[\s\S]*?Handle: (?P<handle>\d+)[\s\S]*?level (?P<level>\d+) state (?P<isCharging>\d+)",
        )
        .unwrap();
        let loaded_re = Regex::new(
            r"(?m)^(?P<dateTime>.+?) INFO.+?_OnDeviceLoaded[\s\S]*?Name: (?P<name>.*)[\s\S]*?Handle: (?P<handle>\d+)",
        )
        .unwrap();
        let removed_re = Regex::new(
            r"(?m)^(?P<dateTime>.+?) INFO.+?_OnDeviceRemoved[\s\S]*?Name: (?P<name>.*)[\s\S]*?Handle: (?P<handle>\d+)",
        )
        .unwrap();

        // Last match per handle wins; connection = loaded after removed (by byte offset).
        let last_by_handle = |re: &Regex| -> HashMap<String, (usize, regex::Captures)> {
            let mut map = HashMap::new();
            for m in re.captures_iter(&log) {
                let handle = m.name("handle").unwrap().as_str().to_owned();
                let start = m.get(0).unwrap().start();
                map.insert(handle, (start, m));
            }
            map
        };

        let battery = last_by_handle(&battery_re);
        let loaded = last_by_handle(&loaded_re);
        let removed = last_by_handle(&removed_re);

        let shown = crate::config::load().shown_device_handle;
        let mut devices = self.devices.lock().unwrap();
        for (handle, (_, caps)) in &battery {
            let name = caps.name("name").unwrap().as_str().to_owned();
            let level: u8 = caps
                .name("level")
                .unwrap()
                .as_str()
                .parse()
                .unwrap_or(0)
                .min(100);
            let charging = caps.name("isCharging").unwrap().as_str() != "0";
            // TS parity (`watcherV3.ts`): missing events count as index -1,
            // so a battery-only device with no load/remove info stays
            // disconnected (-1 > -1 == false) instead of showing stale state.
            let loaded_idx = loaded.get(handle).map(|(i, _)| *i as i64).unwrap_or(-1);
            let removed_idx = removed.get(handle).map(|(i, _)| *i as i64).unwrap_or(-1);
            let connected = loaded_idx > removed_idx;
            // V3 has no category: classify by product-name keywords.
            let kind = crate::battery::device_kind("", &name);
            devices.insert(
                handle.clone(),
                RazerDevice {
                    name,
                    handle: handle.clone(),
                    battery_percentage: level,
                    is_charging: charging,
                    is_connected: connected,
                    is_selected: shown.is_empty() || shown == *handle,
                    kind,
                },
            );
        }
    }

    fn parse_v4(&self) {
        let Some(dir) = Self::v4_log_dir() else {
            return;
        };
        let Some(path) = Self::latest_v4_log(&dir) else {
            return;
        };
        let Ok(log) = fs::read_to_string(&path) else {
            return;
        };
        let line_re =
            Regex::new(r"(?m)^\[(?P<timestamp>.+?)\].*connectingDeviceData: (?P<json>.+)$").unwrap();
        // Every snapshot in the file, oldest first (TS `watcherV4.ts` replays
        // the whole history so devices missing from the latest snapshot stay
        // known, marked disconnected).
        let mut snapshots: Vec<(String, String)> = Vec::new();
        for caps in line_re.captures_iter(&log) {
            snapshots.push((
                caps.name("timestamp").unwrap().as_str().to_owned(),
                caps.name("json").unwrap().as_str().to_owned(),
            ));
        }
        let Some((last_ts, last_json)) = snapshots.last().cloned() else {
            return;
        };
        {
            let known = self.last_v4_timestamp.lock().unwrap();
            if *known == last_ts {
                return;
            }
        }
        // A corrupt latest line must NOT advance the timestamp (TS throws out
        // of the whole parse): retry it on the next pass instead of freezing
        // every device as disconnected.
        let Ok(last_vals) = serde_json::from_str::<Vec<serde_json::Value>>(&last_json) else {
            return;
        };

        // Connection = membership in the LAST snapshot, matching either id
        // (mirrors `lastMatch.info.some(serial === handle || container === handle)`).
        let mut connected_ids: std::collections::HashSet<String> =
            std::collections::HashSet::new();
        for v in &last_vals {
            if let Ok(d) = serde_json::from_value::<V4Device>(v.clone()) {
                if !d.serial_number.is_empty() {
                    connected_ids.insert(d.serial_number.clone());
                }
                if !d.device_container_id.is_empty() {
                    connected_ids.insert(d.device_container_id.clone());
                }
            }
        }

        let shown = crate::config::load().shown_device_handle;
        let mut devices = self.devices.lock().unwrap();
        for (_, json) in &snapshots {
            let Ok(vals) = serde_json::from_str::<Vec<serde_json::Value>>(json) else {
                continue;
            };
            for v in vals {
                let Ok(d) = serde_json::from_value::<V4Device>(v) else {
                    continue;
                };
                if !d.has_battery {
                    continue;
                }
                // TS: `x.serialNumber ?? x.deviceContainerId` (null falls back
                // to the container id); empty strings fall back too.
                let handle = if d.serial_number.is_empty() {
                    d.device_container_id.clone()
                } else {
                    d.serial_number.clone()
                };
                if handle.is_empty() {
                    continue;
                }
                devices.insert(
                    handle.clone(),
                    RazerDevice {
                        name: d.name.en.clone(),
                        handle: handle.clone(),
                        battery_percentage: d.power_status.level.min(100),
                        is_charging: d.power_status.charging_status == "Charging",
                        is_connected: connected_ids.contains(&handle),
                        is_selected: shown.is_empty() || shown == handle,
                        kind: crate::battery::device_kind(&d.category, &d.name.en),
                    },
                );
            }
        }
        // Drop the NOSERIALNUMBER duplicate once the real serial resolves (TS parity).
        if let Some(no_serial) = devices.get("NOSERIALNUMBER").cloned() {
            if devices
                .values()
                .any(|d| d.handle != "NOSERIALNUMBER" && d.name == no_serial.name)
            {
                devices.remove("NOSERIALNUMBER");
            }
        }
        *self.last_v4_timestamp.lock().unwrap() = last_ts;
    }
}

/// One entry of a V4 `connectingDeviceData` snapshot.
///
/// `rename_all = "camelCase"` is load-bearing: the log keys are
/// `serialNumber` / `hasBattery` / `deviceContainerId` / `powerStatus` /
/// `chargingStatus`. Without it every field silently falls back to its
/// default (`has_battery == false`), the battery filter drops all devices,
/// and the widget stays at `--` forever.
/// `null_to_default` on each field tolerates explicit JSON nulls the same way.
#[derive(Debug, Clone, Default, serde::Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct V4Device {
    #[serde(default, deserialize_with = "null_to_default")]
    serial_number: String,
    #[serde(default, deserialize_with = "null_to_default")]
    has_battery: bool,
    #[serde(default, deserialize_with = "null_to_default")]
    device_container_id: String,
    #[serde(default, deserialize_with = "null_to_default")]
    power_status: V4Power,
    #[serde(default, deserialize_with = "null_to_default")]
    name: V4Name,
    /// "MOUSE" / "KEYBOARD" / "HEADSET" / … — drives the device-type icon.
    #[serde(default, deserialize_with = "null_to_default")]
    category: String,
}

#[derive(Debug, Clone, Default, serde::Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct V4Power {
    #[serde(default, deserialize_with = "null_to_default")]
    charging_status: String,
    #[serde(default, deserialize_with = "null_to_default")]
    level: u8,
}

#[derive(Debug, Clone, Default, serde::Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct V4Name {
    #[serde(default, deserialize_with = "null_to_default")]
    en: String,
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Real shape from `systray_systrayv2*.log` (camelCase keys + nulls).
    const SAMPLE: &str = r#"[{"serialNumber":"632516H31000044","hasBattery":true,"deviceContainerId":"{9E502CF7-160A-51EA-8250-14BD19EB4A4A}","powerStatus":{"chargingStatus":"NoCharge_BatteryFull","level":82},"name":{"en":"Razer Viper V3 HyperSpeed"},"category":"MOUSE"},{"serialNumber":null,"hasBattery":true,"deviceContainerId":"{9E502CF7-160A-51EA-8250-14BD19EB4A4A}","powerStatus":null,"name":null,"category":null}]"#;

    #[test]
    fn v4_camel_case_fields_deserialize() {
        let devs: Vec<V4Device> = serde_json::from_str(SAMPLE).unwrap();
        assert_eq!(devs.len(), 2);
        assert!(devs[0].has_battery);
        assert_eq!(devs[0].serial_number, "632516H31000044");
        assert_eq!(devs[0].power_status.level, 82);
        assert_eq!(devs[0].name.en, "Razer Viper V3 HyperSpeed");
        assert_eq!(devs[0].category, "MOUSE");
    }

    #[test]
    fn v4_category_drives_device_kind() {
        let devs: Vec<V4Device> = serde_json::from_str(SAMPLE).unwrap();
        assert_eq!(
            crate::battery::device_kind(&devs[0].category, &devs[0].name.en),
            crate::battery::DeviceKind::Mouse
        );
        // Null category + unknown name falls back to Other.
        assert_eq!(
            crate::battery::device_kind(&devs[1].category, &devs[1].name.en),
            crate::battery::DeviceKind::Other
        );
    }

    #[test]
    fn v4_nulls_fall_back_to_defaults() {
        let devs: Vec<V4Device> = serde_json::from_str(SAMPLE).unwrap();
        assert_eq!(devs[1].serial_number, "");
        assert_eq!(devs[1].power_status.level, 0);
        assert_eq!(devs[1].name.en, "");
        // Null serial falls back to the container id (TS `??` parity).
        let handle = if devs[1].serial_number.is_empty() {
            devs[1].device_container_id.clone()
        } else {
            devs[1].serial_number.clone()
        };
        assert_eq!(handle, "{9E502CF7-160A-51EA-8250-14BD19EB4A4A}");
    }

    #[test]
    fn v4_charging_flag_matches_ts_strict_equality() {
        for (status, expected) in [
            ("Charging", true),
            ("NoCharge_BatteryFull", false),
            ("off", false),
            ("", false),
        ] {
            let json = format!(
                r#"[{{"serialNumber":"S","hasBattery":true,"deviceContainerId":"C","powerStatus":{{"chargingStatus":{status:?},"level":50}},"name":{{"en":"N"}}}}]"#
            );
            let devs: Vec<V4Device> = serde_json::from_str(&json).unwrap();
            assert_eq!(devs[0].power_status.charging_status == "Charging", expected, "{status}");
        }
    }

    #[test]
    fn latest_v4_log_picks_highest_index() {
        let dir = std::env::temp_dir().join(format!("razer-taskbar-test-{}", std::process::id()));
        let _ = std::fs::create_dir_all(&dir);
        for name in ["systray_systrayv2.log", "systray_systrayv21.log", "systray_systrayv24.log"] {
            let _ = std::fs::write(dir.join(name), "x");
        }
        let latest = RazerWatcher::latest_v4_log(&dir).unwrap();
        assert_eq!(latest.file_name().unwrap(), "systray_systrayv24.log");
        let _ = std::fs::remove_dir_all(&dir);
    }
}
