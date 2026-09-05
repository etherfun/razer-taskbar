// Port of src/i18n.rs: the English UI strings double as translation keys —
// Tr("Exit") returns the key itself for English and the mapped string for the
// configured language. The language comes from settings ("auto" | "en" |
// "zh"); "auto" follows the Windows UI language. Compact durations (h/m/d)
// stay locale-neutral by design.

using System.Runtime.InteropServices;

namespace RazerTaskbar.Core;

public enum LanguageSetting
{
    Auto = 0,
    En = 1,
    Zh = 2,
}

public static class I18n
{
    private static int _setting = (int)LanguageSetting.Auto;
    private static int _resolved = (int)LanguageSetting.En;

    /// <summary>Raised on SetSetting so open UI surfaces can re-localize.</summary>
    public static event Action? LanguageChanged;

    /// <summary>Read `language` from settings and resolve it against the system
    /// UI language. Called once at startup.</summary>
    public static void Init()
    {
        SetSetting(ConfigService.Load().Language switch
        {
            "en" => LanguageSetting.En,
            "zh" => LanguageSetting.Zh,
            _ => LanguageSetting.Auto,
        });
    }

    public static void SetSetting(LanguageSetting v)
    {
        _setting = (int)v;
        _resolved = (int)(v == LanguageSetting.Auto ? Detect() : v);
        LanguageChanged?.Invoke();
    }

    /// <summary>Windows UI language: zh-CN/zh-TW/... all share primary lang 0x04.</summary>
    private static LanguageSetting Detect()
    {
        ushort langid = GetUserDefaultUILanguage();
        return (langid & 0xFF) == 0x04 ? LanguageSetting.Zh : LanguageSetting.En;
    }

    /// <summary>Translate a UI string. Unknown keys pass through unchanged.</summary>
    public static string Tr(string s) => IsZh ? Translate(s) : s;

    public static bool IsZh => _resolved == (int)LanguageSetting.Zh;

    private static string Translate(string s) => s switch
    {
        // Menu (WidgetWindow)
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
        // Tooltip (TrayIcon)
        "(charging)" => "（充电中）",
        "No devices found." => "未找到设备。",
        // Hover panel
        "No Razer devices found" => "未找到 Razer 设备",
        // Estimates (HistoryService)
        "~{} left" => "剩余约 {}",
        "full in {}" => "充满还需 {}",
        // History page
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
        "No data yet — recording starts when a device connects." => "暂无数据——设备连接后开始记录。",
        "charging" => "充电",
        "discharging" => "放电",
        "off (excluded)" => "关机（不计入）",
        "Cycles & charging sessions ({} recorded)" => "周期与充电会话（共 {} 条）",
        "USE" => "用电",
        "CHARGE" => "充电",
        // Settings page
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
    };

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern ushort GetUserDefaultUILanguage();
}
