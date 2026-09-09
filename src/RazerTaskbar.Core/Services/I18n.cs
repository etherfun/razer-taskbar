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

    /// <summary>zh table: English key → 中文. A dictionary (not a switch)
    /// so tests can enumerate it — I18nTests checks both directions: every
    /// Tr call-site literal must be a key, and every key must still be
    /// referenced from src.</summary>
    public static IReadOnlyDictionary<string, string> ZhMap { get; } = new Dictionary<string, string>
    {
        // Menu (WidgetWindow)
        ["Lowest battery device"] = "当前电量最低设备",
        ["Poll interval"] = "刷新间隔",
        ["Run at startup"] = "开机自启",
        ["Show widget"] = "显示挂件",
        ["Show the battery widget on the taskbar. Off leaves the tray icon only."] = "在任务栏显示电池挂件，关闭后仅保留托盘图标。",
        ["Show tray icon"] = "显示托盘图标",
        ["Show devices on hover"] = "悬停显示设备列表",
        ["Record battery history"] = "记录电量历史",
        ["Record interval"] = "记录间隔",
        ["Show time remaining on widget"] = "在挂件上显示剩余时间",
        ["Colored battery icon"] = "彩色电量图标",
        ["Battery history"] = "电量历史",
        ["Language"] = "语言",
        ["Auto"] = "自动",
        ["Exit"] = "退出",
        // Tooltip (TrayIcon)
        ["(charging)"] = "（充电中）",
        ["No devices found"] = "未找到设备",
        // Hover panel
        ["No Razer devices found"] = "未找到 Razer 设备",
        // Estimates (HistoryService)
        ["~{} left"] = "剩余约 {}",
        ["full in {}"] = "充满还需 {}",
        // History page
        ["Battery history — Razer Taskbar"] = "电量历史 — Razer Taskbar",
        ["7 days"] = "7 天",
        ["30 days"] = "30 天",
        ["All"] = "全部",
        ["discharge cycles"] = "放电周期",
        ["per full charge"] = "单次充电可用",
        ["time remaining now"] = "当前剩余可用",
        ["until full (now)"] = "距充满（当前）",
        ["Battery level"] = "电量曲线",
        ["No data yet — recording starts when a device connects."] = "暂无数据——设备连接后开始记录。",
        ["charging"] = "充电",
        ["discharging"] = "放电",
        ["off"] = "关机",
        ["usable per 100% charge"] = "每 100% 电量可用",
        ["compare device"] = "对比设备",
        ["Show off periods"] = "显示关机时段",
        ["Discharge cycles completed within the selected range. A cycle runs from one charge session to the next; off periods are not counted."] = "所选范围内完成的放电周期数。一个周期从一次充电会话结束到下一次开始；关机时段不计入。",
        ["Estimated usable time per 100% of charge, from the discharge cycles in range. Recent cycles count most — weight decays with a 30-day half-life (tracking battery aging and habit changes)."] = "按范围内放电周期估算的每 100% 电量可用时长。近期周期权重更高——按 30 天半衰期衰减（贴合电池老化与使用习惯变化）。",
        ["Estimated time to fully charge the device, from the charge sessions in range."] = "按范围内充电会话估算的充满设备所需时长。",
        ["Estimated usable time right now (discharging) or time until full (charging), anchored at the current level and counting down in real time."] = "当前预估可用时长（放电中）或距充满时长（充电中），以当前电量锚定并实时倒计时。",
        ["Cycles & charging sessions ({} recorded)"] = "周期与充电会话（共 {} 条）",
        ["USE"] = "用电",
        ["CHARGE"] = "充电",
        // Battery health (history page)
        ["battery health"] = "电池健康",
        ["estimated capacity"] = "估算容量",
        ["fade per month"] = "每月衰减",
        ["est. to 80%"] = "预计到 80%",
        ["stable"] = "稳定",
        ["at 80% now"] = "已达 80%",
        ["to 80% in {}"] = "约 {} 到 80%",
        ["{} mo"] = "{} 个月",
        ["<1 mo"] = "不足 1 个月",
        ["insufficient data"] = "数据不足",
        ["Capacity estimated from charge speed relative to the earliest recorded sessions — charge current is usage-independent, so charge speed isolates capacity fade. Percentage readings quantize coarsely; values are approximate."] = "以充电速度相对最早记录期估算容量——充电电流与使用强度无关，故充电速度能单独反映容量衰减。电量百分比读数量化较粗，数值仅供参考。",
        // Settings row descriptions
        ["Which device's battery the widget displays."] = "挂件显示哪台设备的电量。",
        ["Anchor the widget on the left or right side of the taskbar."] = "将挂件锚定在任务栏左侧或右侧。",
        ["Embed in widgets free space"] = "嵌入小组件空余空间",
        ["Place the widget inside the Windows widgets button's empty area (right of the weather) instead of beside it."] = "开启后挂件嵌入任务栏小组件按钮（天气）的空余区域内；关闭时在小组件外侧独立摆放。",
        ["Embed into the taskbar"] = "嵌入任务栏",
        ["Recreate the widget as a real child of the taskbar band (survives fullscreen apps). Experimental."] = "将挂件窗口销毁重建为任务栏带的真正子窗口，全屏应用下不再被遮挡。实验特性。",
        ["Second row with the predicted remaining / time-to-full duration."] = "第二行显示预计剩余 / 充满时长。",
        ["Charging / saver keep their state colors; this adds the green-to-red level gradient."] = "充电/省电状态色始终显示；开启后普通模式电量按绿→红渐变着色。",
        ["Fade transition"] = "切换过渡动画",
        ["Cross-fade the widget when the displayed device changes."] = "显示设备变化时，挂件画面淡入淡出过渡。",
        ["Notification-area icon with tooltip and menu."] = "带提示与菜单的通知区图标。",
        ["Hover the widget to list every known device."] = "悬停挂件列出所有已知设备。",
        ["Display refresh cadence (seconds) when there is no log activity."] = "无日志活动时的显示刷新间隔(秒)。",
        ["Sample battery.db and predict usage time."] = "采样 battery.db 并预测可用时长。",
        ["Sampling cadence while recording is enabled (seconds)."] = "记录启用时的采样间隔(秒)。",
        ["UI language. Auto follows the Windows UI language."] = "界面语言。自动跟随 Windows 界面语言。",
        ["Register HKCU\\...\\Run\\RazerTaskbar."] = "写入注册表 HKCU\\...\\Run\\RazerTaskbar 自启动项。",
        // History page export + compare
        ["Export"] = "导出",
        ["Time range"] = "时间范围",
        ["compare"] = "对比",
        ["None"] = "无",
        ["CSV exported ({})"] = "CSV 已导出({})",
        ["No data to export"] = "暂无可导出的数据",
        ["Export failed"] = "导出失败",
        // Settings page
        ["Settings"] = "设置",
        ["Settings — Razer Taskbar"] = "设置 — Razer Taskbar",
        ["Widget"] = "挂件",
        ["History"] = "电量记录",
        ["General"] = "通用",
        ["Shown device"] = "显示设备",
        // Display modes (settings page)
        ["Display mode"] = "显示模式",
        ["How the widget picks which device to show."] = "挂件选择显示设备的方式。",
        ["Fixed device"] = "固定设备",
        ["Swap on battery drop"] = "电量下降时临时替换",
        ["Rotate all devices"] = "定时轮播全部设备",
        ["Swap duration"] = "替换显示时长",
        ["How long a dropped device stays shown before switching back."] = "设备电量下降后临时显示的时长，到期切回显示设备。",
        ["Rotate interval"] = "轮播间隔",
        ["How long each device stays shown before rotating to the next."] = "每台设备的显示时长，到期轮换到下一台。",
        ["Widget side"] = "挂件位置",
        ["Left"] = "左侧",
        ["Right"] = "右侧",
    };

    private static string Translate(string s) => ZhMap.TryGetValue(s, out var zh) ? zh : s;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern ushort GetUserDefaultUILanguage();
}
