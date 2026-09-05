//! Minimal i18n: the English UI strings double as translation keys —
//! `tr("Exit")` returns the key itself for English and the mapped string for
//! the configured language. No dependencies. The language comes from
//! settings (`language`: "auto" | "en" | "zh"); "auto" follows the Windows
//! UI language. Compact durations (h/m/d) stay locale-neutral by design.

use std::sync::atomic::{AtomicU8, Ordering};

pub const AUTO: u8 = 0;
pub const EN: u8 = 1;
pub const ZH: u8 = 2;

static SETTING: AtomicU8 = AtomicU8::new(AUTO);
static RESOLVED: AtomicU8 = AtomicU8::new(EN);

/// Read `language` from settings and resolve it against the system UI
/// language. Called once at startup.
pub fn init() {
    set_setting(match crate::config::load().language.as_str() {
        "en" => EN,
        "zh" => ZH,
        _ => AUTO,
    });
}

pub fn set_setting(v: u8) {
    SETTING.store(v, Ordering::Relaxed);
    RESOLVED.store(if v == AUTO { detect() } else { v }, Ordering::Relaxed);
}

/// Windows UI language: zh-CN/zh-TW/... all share primary lang 0x04.
fn detect() -> u8 {
    unsafe {
        let langid = windows::Win32::Globalization::GetUserDefaultUILanguage();
        if langid & 0xFF == 0x04 {
            ZH
        } else {
            EN
        }
    }
}

/// Translate a UI string. Unknown keys pass through unchanged (so English
/// callers never break when a key is missing from the zh table).
pub fn tr(s: &'static str) -> &'static str {
    if !is_zh() {
        return s;
    }
    translate(s)
}

pub fn is_zh() -> bool {
    RESOLVED.load(Ordering::Relaxed) == ZH
}

fn translate(s: &'static str) -> &'static str {
    match s {
        // Menu (window.rs)
        "All devices" => "所有设备",
        "Poll interval" => "刷新间隔",
        "Widget on left" => "挂件靠左",
        "Widget on right" => "挂件靠右",
        "Run at startup" => "开机自启",
        "Show tray icon" => "显示托盘图标",
        "Show devices on hover" => "悬停显示设备列表",
        "Record battery history" => "记录电量历史",
        "Record interval" => "记录间隔",
        "Show time remaining on widget" => "在挂件上显示剩余时间",
        "Battery history…" => "电量历史…",
        "Language" => "语言",
        "Auto" => "自动",
        "Exit" => "退出",
        // Tooltip (tray.rs)
        "(charging)" => "（充电中）",
        "No devices found." => "未找到设备。",
        // Hover panel (hover.rs)
        "No Razer devices found" => "未找到 Razer 设备",
        // Estimates (history.rs)
        "~{} left" => "剩余约 {}",
        "full in {}" => "充满还需 {}",
        // Viewer (viewer.rs)
        "Battery history — Razer Taskbar" => "电量历史 — Razer Taskbar",
        "7 days" => "7 天",
        "30 days" => "30 天",
        "All" => "全部",
        "discharge cycles" => "放电周期",
        "per full charge" => "单次充电可用",
        "empty → full" => "空电 → 充满",
        "time remaining now" => "当前剩余可用",
        "until full (now)" => "距充满（当前）",
        "Battery level" => "电量曲线",
        "No data yet — recording starts when a device connects." => {
            "暂无数据——设备连接后开始记录。"
        }
        "charging" => "充电",
        "discharging" => "放电",
        "off (excluded)" => "关机（不计入）",
        "Cycles & charging sessions ({} recorded)" => "周期与充电会话（共 {} 条）",
        "USE" => "用电",
        "CHARGE" => "充电",
        // Settings window (settings.rs) + the trimmed menu entry
        "Settings…" => "设置…",
        "Settings — Razer Taskbar" => "设置 — Razer Taskbar",
        "Widget" => "挂件",
        "History" => "电量记录",
        "General" => "通用",
        "Shown device" => "显示设备",
        "Widget side" => "挂件位置",
        "Left" => "左侧",
        "Right" => "右侧",
        _ => s,
    }
}

#[cfg(test)]
pub(crate) fn test_lock() -> std::sync::MutexGuard<'static, ()> {
    static LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());
    LOCK.lock().unwrap()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn translation_basics() {
        let _g = test_lock();
        set_setting(EN);
        assert_eq!(tr("Exit"), "Exit");
        assert_eq!(tr("unknown key"), "unknown key");
        set_setting(ZH);
        assert_eq!(tr("Exit"), "退出");
        assert_eq!(tr("Poll interval"), "刷新间隔");
        // Missing key falls back to the English source.
        assert_eq!(tr("unknown key"), "unknown key");
        // Restore the default so other tests are unaffected.
        set_setting(AUTO);
    }
}
