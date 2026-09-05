// Port of src/config.rs: JSON config in `%APPDATA%/razer-taskbar/settings.json`
// + Run-key autostart. Every field carries the same default the Rust
// `#[serde(default)]` impl provides, so old settings.json files stay loadable.

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace RazerTaskbar.Core;

public sealed class Config
{
    [JsonPropertyName("run_at_startup")]
    public bool RunAtStartup { get; set; }

    [JsonPropertyName("polling_throttle_secs")]
    public ulong PollingThrottleSecs { get; set; } = 15;

    [JsonPropertyName("shown_device_handle")]
    public string ShownDeviceHandle { get; set; } = "";

    /// <summary>"auto" | "v3" | "v4"</summary>
    [JsonPropertyName("synapse_version")]
    public string SynapseVersion { get; set; } = "auto";

    /// <summary>"left" | "right" — widget side relative to TrayNotifyWnd.</summary>
    [JsonPropertyName("widget_side")]
    public string WidgetSide { get; set; } = "right";

    [JsonPropertyName("avoid_overlap_with_widgets")]
    public bool AvoidOverlapWithWidgets { get; set; } = true;

    /// <summary>Reserved space after Start for the (XAML-invisible) taskbar
    /// app icons on Win11.</summary>
    [JsonPropertyName("taskbar_left_space_win11")]
    public int TaskbarLeftSpaceWin11 { get; set; } = 160;

    [JsonPropertyName("taskbar_right_space_win11")]
    public int TaskbarRightSpaceWin11 { get; set; } = 88;

    [JsonPropertyName("window_offset_left")]
    public int WindowOffsetLeft { get; set; }

    [JsonPropertyName("window_offset_top")]
    public int WindowOffsetTop { get; set; }

    [JsonPropertyName("show_tray_icon")]
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>Hover popover on the widget listing every device.</summary>
    [JsonPropertyName("hover_devices")]
    public bool HoverDevices { get; set; } = true;

    /// <summary>Record battery samples to battery.db and predict usage time.</summary>
    [JsonPropertyName("record_battery_history")]
    public bool RecordBatteryHistory { get; set; } = true;

    /// <summary>Show the predicted remaining time as the widget's second row.</summary>
    [JsonPropertyName("show_estimated_time")]
    public bool ShowEstimatedTime { get; set; } = true;

    /// <summary>Watcher cadence while recording is enabled — finer than the
    /// display poll so connect/charge/level transitions are timestamped
    /// precisely.</summary>
    [JsonPropertyName("history_poll_interval_secs")]
    public ulong HistoryPollIntervalSecs { get; set; } = 5;

    /// <summary>UI language: "auto" (follow Windows) | "en" | "zh".</summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = "auto";

    /// <summary>Experimental: parent the widget into the taskbar band as a
    /// WS_CHILD instead of a topmost overlay.</summary>
    [JsonPropertyName("embed_into_taskbar")]
    public bool EmbedIntoTaskbar { get; set; }
}

public static class ConfigService
{
    private static string BaseDir()
    {
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        return Path.Combine(string.IsNullOrEmpty(appData) ? "." : appData, "razer-taskbar");
    }

    public static string ConfigPath => Path.Combine(BaseDir(), "settings.json");

    /// <summary>Battery history database (shared with the Rust build).</summary>
    public static string DbPath => Path.Combine(BaseDir(), "battery.db");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true, // 2-space pretty print, same shape as serde_json
    };

    public static Config Load()
    {
        string text;
        try
        {
            text = File.ReadAllText(ConfigPath);
        }
        catch (Exception)
        {
            return new Config();
        }

        Config cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<Config>(text) ?? new Config();
        }
        catch (JsonException)
        {
            cfg = new Config();
        }

        // Merge-with-defaults fixups (same as config.rs load()).
        if (cfg.PollingThrottleSecs == 0)
        {
            cfg.PollingThrottleSecs = 15;
        }
        if (cfg.HistoryPollIntervalSecs == 0)
        {
            cfg.HistoryPollIntervalSecs = 5;
        }
        return cfg;
    }

    public static void Save(Config cfg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, JsonOpts));
        }
        catch (Exception)
        {
            // Best effort, like config.rs save().
        }
    }

    /// <summary>Sync HKCU `...\Run\RazerTaskbar` with `run_at_startup`.</summary>
    public static void ApplyAutostart(bool enabled)
    {
        var exe = Environment.ProcessPath ?? "";
        if (exe.Length == 0)
        {
            return;
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null)
            {
                return;
            }
            if (enabled)
            {
                key.SetValue("RazerTaskbar", exe, RegistryValueKind.String);
            }
            else
            {
                try
                {
                    key.DeleteValue("RazerTaskbar");
                }
                catch (Exception)
                {
                    // Absent value: nothing to do.
                }
            }
        }
        catch (Exception)
        {
            // Registry unavailable: ignore like the Rust ok()-swallowed paths.
        }
    }
}
