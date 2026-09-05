//! Device model + display-pick rule (ported from `tray_manager.ts:pickDeviceToDisplay`).

use std::collections::HashMap;

/// What kind of peripheral a device is (drives the type icon in the widget
/// and the hover panel). Synapse V4 logs carry an explicit `category`
/// (MOUSE / KEYBOARD / HEADSET / …); V3 logs carry nothing, so the name is
/// matched against Razer product-line keywords as a fallback.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DeviceKind {
    Headset,
    Mouse,
    Keyboard,
    Other,
}

#[derive(Debug, Clone)]
pub struct RazerDevice {
    pub name: String,
    pub handle: String,
    pub battery_percentage: u8,
    pub is_charging: bool,
    pub is_connected: bool,
    pub is_selected: bool,
    pub kind: DeviceKind,
}

pub type DeviceMap = HashMap<String, RazerDevice>;

/// Classify a device from its Synapse V4 `category` (may be empty — V3 logs
/// have no category), falling back to product-name keywords, then `Other`.
pub fn device_kind(category: &str, name: &str) -> DeviceKind {
    match category.to_ascii_uppercase().as_str() {
        "MOUSE" => return DeviceKind::Mouse,
        "KEYBOARD" => return DeviceKind::Keyboard,
        "HEADSET" | "HEADPHONES" | "EARBUDS" => return DeviceKind::Headset,
        _ => {}
    }
    let n = name.to_lowercase();
    // Order matters only where a name could hit two lists; the lists below
    // are disjoint. First hit wins within each list.
    const HEADSET: &[&str] = &[
        "headset", "headphone", "earbud", "kraken", "blackshark", "barracuda", "opus", "hammerhead",
    ];
    const KEYBOARD: &[&str] = &[
        "keyboard", "huntsman", "blackwidow", "ornata", "cynosa", "deathstalker", "tartarus", "joro",
    ];
    const MOUSE: &[&str] = &[
        "mouse", "viper", "deathadder", "basilisk", "naga", "mamba", "orochi", "lancehead",
        "diamondback", "cobra", "adder", "orca", "atheris",
    ];
    if HEADSET.iter().any(|k| n.contains(k)) {
        return DeviceKind::Headset;
    }
    if KEYBOARD.iter().any(|k| n.contains(k)) {
        return DeviceKind::Keyboard;
    }
    if MOUSE.iter().any(|k| n.contains(k)) {
        return DeviceKind::Mouse;
    }
    DeviceKind::Other
}

/// Pick the user-selected device, else the lowest battery preferring non-charging.
/// Mirrors `a.battery * (a.charging ? 100 : 1)` ordering from the TS original.
pub fn pick_device_to_display(devices: &DeviceMap) -> Option<RazerDevice> {
    let mut candidates: Vec<&RazerDevice> = devices
        .values()
        .filter(|d| d.is_connected && d.is_selected)
        .collect();
    candidates.sort_by_key(|d| d.battery_percentage as u32 * if d.is_charging { 100 } else { 1 });
    candidates.first().cloned().cloned()
}

/// Native Win11 battery glyph (Segoe Fluent Icons, U+E850-E859).
/// Measured by rendering (2026-09): E850-E859 are the 10 level steps with a
/// steadily growing interior fill; **E85A is NOT "full"** in the current
/// font — it renders as an outline with a plug/bolt-like mark that reads as
/// a charging icon, so levels above 90% use E859 (the fullest fill).
pub fn fluent_battery_glyph(level: u8) -> char {
    match level {
        0..=9 => '\u{E850}',
        10..=19 => '\u{E851}',
        20..=29 => '\u{E852}',
        30..=39 => '\u{E853}',
        40..=49 => '\u{E854}',
        50..=59 => '\u{E855}',
        60..=69 => '\u{E856}',
        70..=79 => '\u{E857}',
        80..=89 => '\u{E858}',
        _ => '\u{E859}',
    }
}

/// Win11 Fluent battery fill color by level (native GDI, no PNG assets).
pub fn color_for(level: u8) -> (u8, u8, u8) {
    match level {
        0..=19 => (0xE8, 0x11, 0x23), // red
        20..=39 => (0xFF, 0x8C, 0x00), // orange
        40..=59 => (0xFF, 0xB9, 0x00), // yellow
        60..=79 => (0x6C, 0xCB, 0x5F), // light green
        _ => (0x16, 0xC6, 0x0C),       // green
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn dev(handle: &str, level: u8, charging: bool, selected: bool) -> RazerDevice {
        RazerDevice {
            name: handle.into(),
            handle: handle.into(),
            battery_percentage: level,
            is_charging: charging,
            is_connected: true,
            is_selected: selected,
            kind: DeviceKind::Other,
        }
    }

    #[test]
    fn prefers_non_charging_over_lower_charging() {
        let mut m = DeviceMap::new();
        m.insert("a".into(), dev("a", 90, false, true));
        m.insert("b".into(), dev("b", 10, true, true));
        assert_eq!(pick_device_to_display(&m).unwrap().handle, "a");
    }

    #[test]
    fn respects_selection() {
        let mut m = DeviceMap::new();
        m.insert("a".into(), dev("a", 5, false, false));
        m.insert("b".into(), dev("b", 90, false, true));
        assert_eq!(pick_device_to_display(&m).unwrap().handle, "b");
    }

    #[test]
    fn kind_from_v4_category_beats_name() {
        // A device named like a mouse but categorized as keyboard stays keyboard.
        assert_eq!(device_kind("KEYBOARD", "Razer Viper"), DeviceKind::Keyboard);
        assert_eq!(device_kind("MOUSE", "Razer Viper V3 HyperSpeed"), DeviceKind::Mouse);
        assert_eq!(device_kind("headset", "Razer Thing"), DeviceKind::Headset);
    }

    #[test]
    fn kind_falls_back_to_name_keywords() {
        // V3 logs carry no category: product-line keywords decide.
        assert_eq!(device_kind("", "Razer BlackShark V2 Pro"), DeviceKind::Headset);
        assert_eq!(device_kind("", "Razer Kraken"), DeviceKind::Headset);
        assert_eq!(device_kind("", "Razer DeathAdder V3"), DeviceKind::Mouse);
        assert_eq!(device_kind("", "Razer Basilisk"), DeviceKind::Mouse);
        assert_eq!(device_kind("", "Razer Huntsman Mini"), DeviceKind::Keyboard);
        assert_eq!(device_kind("", "Razer BlackWidow"), DeviceKind::Keyboard);
        assert_eq!(device_kind("", "Razer Joro"), DeviceKind::Keyboard);
        // Unknown product: Other.
        assert_eq!(device_kind("", "Razer Chroma Mug"), DeviceKind::Other);
        assert_eq!(device_kind("MOUSE_DOCK", "Razer Mouse Dock"), DeviceKind::Mouse);
    }
}
