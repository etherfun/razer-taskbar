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
    /// Device-side battery saver / low-power mode (Synapse `lowPowerMode`).
    pub battery_saver: bool,
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

/// Which glyph series the battery uses. All three are 11-glyph sets, one
/// per 10% step (verified mapped + rendering in the installed
/// SegoeIcons.ttf).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BatteryGlyphState {
    /// EBA0-EBAA: normal level batteries (0% .. 100%).
    Normal,
    /// EBAB-EBB5: charging batteries (bolt inside).
    Charging,
    /// EBB6-EBC0: battery-saver batteries (leaf inside).
    Saver,
}

/// Win11 battery glyph for `level` (0-100) in `state`.
pub fn battery_glyph(level: u8, state: BatteryGlyphState) -> char {
    let idx = ((level as u32).min(100) + 5) / 10; // 0..=10, rounded to 10%
    let base = match state {
        BatteryGlyphState::Normal => 0xEBA0,
        BatteryGlyphState::Charging => 0xEBAB,
        BatteryGlyphState::Saver => 0xEBB6,
    };
    char::from_u32(base + idx).unwrap_or('\u{EBA0}')
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
            battery_saver: false,
            is_connected: true,
            is_selected: selected,
            kind: DeviceKind::Other,
        }
    }

    #[test]
    fn battery_glyph_series_mapping() {
        use BatteryGlyphState::*;
        // Normal: EBA0-EBAA, one glyph per 10% (EBAA is truly full).
        assert_eq!(battery_glyph(0, Normal), '\u{EBA0}');
        assert_eq!(battery_glyph(72, Normal), '\u{EBA7}');
        assert_eq!(battery_glyph(100, Normal), '\u{EBAA}');
        // Charging / saver: 11-glyph series, 100% hits the last one.
        assert_eq!(battery_glyph(0, Charging), '\u{EBAB}');
        assert_eq!(battery_glyph(72, Charging), '\u{EBB2}');
        assert_eq!(battery_glyph(100, Charging), '\u{EBB5}');
        assert_eq!(battery_glyph(30, Saver), '\u{EBB9}');
        assert_eq!(battery_glyph(100, Saver), '\u{EBC0}');
        // Rounding: 0-4% stays on the empty glyph, 5% rounds up a step.
        assert_eq!(battery_glyph(4, Normal), '\u{EBA0}');
        assert_eq!(battery_glyph(5, Normal), '\u{EBA1}');
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
