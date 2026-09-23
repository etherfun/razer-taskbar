//! Port of src/settings.rs as a WinUI3 page following the Win11 settings
//! conventions (section headers + title/description rows + right-aligned
//! controls). The apply-immediately model is preserved: every handler edits
//! the authoritative config on the widget thread and persists settings.json;
//! the watcher and other consumers pick changes up on their own cadence.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using RazerTaskbar.Core;
using RazerTaskbar.Native;

namespace RazerTaskbar.Features.ControlPanel;

public sealed partial class SettingsPage : Page
{
    private static readonly int[] PollChoices = [5, 10, 15, 30, 60];
    private static readonly int[] RecordChoices = [1, 2, 5, 10, 30];
    private static readonly int[] SwapChoices = [15, 30, 60, 120];
    private static readonly int[] RotateChoices = [10, 30, 60, 120];

    /// <summary>"30 s" / "2 min" — durations stay locale-neutral like the
    /// poll-interval combo.</summary>
    private static string FormatSecs(int p) => p >= 60 && p % 60 == 0 ? $"{p / 60} min" : $"{p} s";

    /// <summary>Programmatic ComboBox SelectionChanged needs an echo guard
    /// during init/refresh (Win32 had none because BM_SETCHECK doesn't fire).</summary>
    private bool _suppress;

    public SettingsPage()
    {
        InitializeComponent();
        // Same reason as HistoryPage: keep the one tree this window built.
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        Localize();
    }

    /// <summary>The page tree is cached (Required), so the ctor runs once
    /// per window: a language switched while the OTHER tab was open would
    /// otherwise surface here as stale strings — re-localize on every
    /// navigation into this page.</summary>
    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Localize();
    }

    /// <summary>Re-localize and re-sync all controls (open + language switch).</summary>
    public void Localize()
    {
        _suppress = true;
        try
        {
            var cfg = AppState.Instance.ConfigSnapshot();

            // Section headers + row labels.
            HeaderWidget.Text = I18n.Tr("Widget");
            HeaderHistory.Text = I18n.Tr("History");
            HeaderGeneral.Text = I18n.Tr("General");
            LblWidget.Text = I18n.Tr("Show widget");
            DescWidget.Text = I18n.Tr("Show the battery widget on the taskbar. Off leaves the tray icon only.");
            LblShownDevice.Text = I18n.Tr("Shown device");
            DescShownDevice.Text = I18n.Tr("Which device's battery the widget displays.");
            LblDisplayMode.Text = I18n.Tr("Display mode");
            DescDisplayMode.Text = I18n.Tr("How the widget picks which device to show.");
            LblSwapDur.Text = I18n.Tr("Swap duration");
            DescSwapDur.Text = I18n.Tr("How long a dropped device stays shown before switching back.");
            LblRotI.Text = I18n.Tr("Rotate interval");
            DescRotI.Text = I18n.Tr("How long each device stays shown before rotating to the next.");
            LblSide.Text = I18n.Tr("Widget side");
            DescSide.Text = I18n.Tr("Anchor the widget on the left or right side of the taskbar.");
            LblEmbedWidgets.Text = I18n.Tr("Embed in widgets free space");
            DescEmbedWidgets.Text = I18n.Tr("Place the widget inside the Windows widgets button's empty area (right of the weather) instead of beside it.");
            LblEmbedTaskbar.Text = I18n.Tr("Embed into the taskbar");
            DescEmbedTaskbar.Text = I18n.Tr("Recreate the widget as a real child of the taskbar band (survives fullscreen apps). Experimental.");
            LblEst.Text = I18n.Tr("Show time remaining on widget");
            DescEst.Text = I18n.Tr("Second row with the predicted remaining / time-to-full duration.");
            LblColor.Text = I18n.Tr("Colored battery icon");
            DescColor.Text = I18n.Tr("Charging / saver keep their state colors; this adds the green-to-red level gradient.");
            LblFade.Text = I18n.Tr("Fade transition");
            DescFade.Text = I18n.Tr("Cross-fade the widget when the displayed device changes.");
            LblTray.Text = I18n.Tr("Show tray icon");
            DescTray.Text = I18n.Tr("Notification-area icon with tooltip and menu.");
            LblHover.Text = I18n.Tr("Show devices on hover");
            DescHover.Text = I18n.Tr("Hover the widget to list every known device.");
            LblPoll.Text = I18n.Tr("Poll interval");
            DescPoll.Text = I18n.Tr("Display refresh cadence (seconds) when there is no log activity.");
            LblRec.Text = I18n.Tr("Record battery history");
            DescRec.Text = I18n.Tr("Sample battery.db and predict usage time.");
            LblRecI.Text = I18n.Tr("Record interval");
            DescRecI.Text = I18n.Tr("Sampling cadence while recording is enabled (seconds).");
            LblLang.Text = I18n.Tr("Language");
            DescLang.Text = I18n.Tr("UI language. Auto follows the Windows UI language.");
            LblAutostart.Text = I18n.Tr("Run at startup");
            DescAutostart.Text = I18n.Tr("Register HKCU\\...\\Run\\RazerTaskbar.");
            // Widget-side combo: rebuild on language switch, keep the choice.
            var sideIdx = ComboSide.SelectedIndex;
            ComboSide.ItemsSource = new List<string> { I18n.Tr("Left"), I18n.Tr("Right") };
            ComboSide.SelectedIndex = sideIdx >= 0 ? sideIdx : 1;
            // Accessibility: screen readers announce the row label per control.
            AutomationProperties.SetName(ComboDevice, LblShownDevice.Text);
            AutomationProperties.SetName(ComboDisplayMode, LblDisplayMode.Text);
            AutomationProperties.SetName(ComboSwapDur, LblSwapDur.Text);
            AutomationProperties.SetName(ComboRotI, LblRotI.Text);
            AutomationProperties.SetName(ComboSide, LblSide.Text);
            AutomationProperties.SetName(SwitchWidget, LblWidget.Text);
            AutomationProperties.SetName(SwitchEst, LblEst.Text);
            AutomationProperties.SetName(SwitchEmbedWidgets, LblEmbedWidgets.Text);
            AutomationProperties.SetName(SwitchEmbedTaskbar, LblEmbedTaskbar.Text);
            AutomationProperties.SetName(SwitchColor, LblColor.Text);
            AutomationProperties.SetName(SwitchFade, LblFade.Text);
            AutomationProperties.SetName(SwitchTray, LblTray.Text);
            AutomationProperties.SetName(SwitchHover, LblHover.Text);
            AutomationProperties.SetName(ComboPoll, LblPoll.Text);
            AutomationProperties.SetName(SwitchRec, LblRec.Text);
            AutomationProperties.SetName(ComboRecI, LblRecI.Text);
            AutomationProperties.SetName(ComboLang, LblLang.Text);
            AutomationProperties.SetName(SwitchAutostart, LblAutostart.Text);

            // Shown device (0 = auto "Lowest battery device", then connected
            // sorted by name).
            var devices = AppState.Instance.Devices.Snapshot();
            var connected = devices.Values
                .Where(d => d.IsConnected)
                .OrderBy(d => d.Name, StringComparer.Ordinal)
                .ToList();
            var connectedNames = connected.Select(d => d.Name).ToList();
            var entries = new List<(string Handle, string Label)> { ("", I18n.Tr("Lowest battery device")) };
            entries.AddRange(connected.Select(d =>
                (d.Handle, $"{DeviceLabels.Label(d.Name, d.Handle, connectedNames)} — {d.BatteryPercentage}%")));
            ComboDevice.ItemsSource = entries.Select(e => e.Label).ToList();
            ComboDevice.SelectedIndex = 0;
            var shown = cfg.ShownDeviceHandle;
            var idx = entries.FindIndex(e => e.Handle == shown);
            if (idx >= 0)
            {
                ComboDevice.SelectedIndex = idx;
            }

            // Display mode + mode-specific rows.
            ComboDisplayMode.ItemsSource = new List<string>
            {
                I18n.Tr("Fixed device"),
                I18n.Tr("Swap on battery drop"),
                I18n.Tr("Rotate all devices"),
            };
            ComboDisplayMode.SelectedIndex = cfg.DisplayMode switch
            {
                "drop_swap" => 1,
                "rotate" => 2,
                _ => 0,
            };
            ComboSwapDur.ItemsSource = SwapChoices.Select(FormatSecs).ToList();
            ComboSwapDur.SelectedIndex = SwapChoices.Contains((int)cfg.SwapDisplaySecs)
                ? Array.IndexOf(SwapChoices, (int)cfg.SwapDisplaySecs)
                : 1;
            ComboRotI.ItemsSource = RotateChoices.Select(FormatSecs).ToList();
            ComboRotI.SelectedIndex = RotateChoices.Contains((int)cfg.RotateIntervalSecs)
                ? Array.IndexOf(RotateChoices, (int)cfg.RotateIntervalSecs)
                : 1;
            UpdateModeRows(cfg.DisplayMode);

            // Widget side.
            ComboSide.SelectedIndex = cfg.WidgetSide == "left" ? 0 : 1;

            // Toggles.
            SwitchWidget.IsOn = cfg.ShowWidget;
            SwitchEst.IsOn = cfg.ShowEstimatedTime;
            SwitchEmbedWidgets.IsOn = cfg.EmbedIntoWidgetsSpace;
            SwitchEmbedTaskbar.IsOn = cfg.EmbedIntoTaskbar;
            SwitchColor.IsOn = cfg.ColorBatteryIcon;
            SwitchFade.IsOn = cfg.FadeTransition;
            SwitchTray.IsOn = cfg.ShowTrayIcon;
            SwitchHover.IsOn = cfg.HoverDevices;
            SwitchRec.IsOn = cfg.RecordBatteryHistory;
            SwitchAutostart.IsOn = cfg.RunAtStartup;

            // Poll / record intervals.
            ComboPoll.ItemsSource = PollChoices.Select(p => $"{p} s").ToList();
            ComboPoll.SelectedIndex = PollChoices.Contains((int)cfg.PollingThrottleSecs)
                ? Array.IndexOf(PollChoices, (int)cfg.PollingThrottleSecs)
                : 2;
            ComboRecI.ItemsSource = RecordChoices.Select(p => $"{p} s").ToList();
            ComboRecI.SelectedIndex = RecordChoices.Contains((int)cfg.HistoryPollIntervalSecs)
                ? Array.IndexOf(RecordChoices, (int)cfg.HistoryPollIntervalSecs)
                : 2;

            // Language.
            ComboLang.ItemsSource = new List<string> { I18n.Tr("Auto"), "English", "中文" };
            ComboLang.SelectedIndex = cfg.Language switch
            {
                "en" => 1,
                "zh" => 2,
                _ => 0,
            };
        }
        finally
        {
            _suppress = false;
        }
    }

    private void ComboDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress)
        {
            return;
        }
        // Entry 0 = auto "" (lowest battery device); otherwise the connected
        // roster order matches the label list built in Localize.
        int index = ComboDevice.SelectedIndex;
        if (index < 0)
        {
            return;
        }
        string handle = "";
        if (index > 0)
        {
            var connected = AppState.Instance.Devices.Snapshot().Values
                .Where(d => d.IsConnected)
                .OrderBy(d => d.Name, StringComparer.Ordinal)
                .ToList();
            handle = index - 1 < connected.Count ? connected[index - 1].Handle : "";
        }
        AppState.PostSetShownDevice(handle);
    }

    /// <summary>Show/hide the mode-specific rows and disable the shown-device
    /// picker while rotating (the carousel ignores it). Called from Localize
    /// and immediately on mode switches — with the new mode, not the config
    /// snapshot that the posted edit has not landed in yet.</summary>
    private void UpdateModeRows(string mode)
    {
        RowSwapDur.Visibility = mode == "drop_swap" ? Visibility.Visible : Visibility.Collapsed;
        RowRotI.Visibility = mode == "rotate" ? Visibility.Visible : Visibility.Collapsed;
        ComboDevice.IsEnabled = mode != "rotate";
    }

    private void ComboDisplayMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || ComboDisplayMode.SelectedIndex < 0)
        {
            return;
        }
        string mode = ComboDisplayMode.SelectedIndex switch
        {
            1 => "drop_swap",
            2 => "rotate",
            _ => "fixed",
        };
        AppState.PostModifyConfig(c => c.DisplayMode = mode);
        // The runtime strategy state (override window, rotate cursor, battery
        // baselines) belongs to the old mode — restart clean. Reposition since
        // the newly picked device may have a different label width.
        AppState.PostResetModeState();
        AppState.PostReposition();
        UpdateModeRows(mode);
    }

    private void ComboSwapDur_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || ComboSwapDur.SelectedIndex < 0)
        {
            return;
        }
        int secs = SwapChoices[ComboSwapDur.SelectedIndex];
        AppState.PostModifyConfig(c => c.SwapDisplaySecs = (ulong)secs);
    }

    private void ComboRotI_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || ComboRotI.SelectedIndex < 0)
        {
            return;
        }
        int secs = RotateChoices[ComboRotI.SelectedIndex];
        AppState.PostModifyConfig(c => c.RotateIntervalSecs = (ulong)secs);
    }

    private void Side_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || ComboSide.SelectedIndex < 0)
        {
            return;
        }
        string side = ComboSide.SelectedIndex == 0 ? "left" : "right";
        AppState.PostModifyConfig(c => c.WidgetSide = side);
        AppState.PostReposition();
    }

    private void SwitchEmbedWidgets_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress)
        {
            return;
        }
        bool on = SwitchEmbedWidgets.IsOn;
        AppState.PostModifyConfig(c => c.EmbedIntoWidgetsSpace = on);
        AppState.PostReposition(); // anchor depends on the toggle
    }

    private void SwitchEmbedTaskbar_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress)
        {
            return;
        }
        bool on = SwitchEmbedTaskbar.IsOn;
        AppState.PostModifyConfig(c => c.EmbedIntoTaskbar = on);
        AppState.PostReposition(); // the display window is recreated on toggle
    }

    private void SwitchEst_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress)
        {
            return;
        }
        bool on = SwitchEst.IsOn;
        AppState.PostModifyConfig(c => c.ShowEstimatedTime = on);
        AppState.PostReposition(); // width depends on the toggle
    }

    private void SwitchColor_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress)
        {
            return;
        }
        bool on = SwitchColor.IsOn;
        AppState.PostModifyConfig(c => c.ColorBatteryIcon = on);
    }

    private void SwitchFade_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress)
        {
            return;
        }
        bool on = SwitchFade.IsOn;
        AppState.PostModifyConfig(c => c.FadeTransition = on);
        // Toggled off mid-animation: stop blending, leave the current frame.
        if (!on)
        {
            AppState.PostResetFade();
        }
    }

    private void SwitchWidget_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress)
        {
            return;
        }
        bool on = SwitchWidget.IsOn;
        AppState.PostModifyConfig(c => c.ShowWidget = on);
        AppState.PostWidgetSetEnabled(on);
        if (!on)
        {
            AppState.PostHoverHide();
        }
    }

    private void SwitchTray_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress)
        {
            return;
        }
        bool on = SwitchTray.IsOn;
        AppState.PostModifyConfig(c => c.ShowTrayIcon = on);
        AppState.PostTraySetEnabled(on);
    }

    private void SwitchHover_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress)
        {
            return;
        }
        bool on = SwitchHover.IsOn;
        AppState.PostModifyConfig(c => c.HoverDevices = on);
        if (!on)
        {
            AppState.PostHoverHide();
        }
    }

    private void ComboPoll_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || ComboPoll.SelectedIndex < 0)
        {
            return;
        }
        int secs = PollChoices[ComboPoll.SelectedIndex];
        AppState.PostModifyConfig(c => c.PollingThrottleSecs = (ulong)secs);
    }

    private void SwitchRec_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress)
        {
            return;
        }
        bool on = SwitchRec.IsOn;
        AppState.PostModifyConfig(c => c.RecordBatteryHistory = on);
    }

    private void ComboRecI_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || ComboRecI.SelectedIndex < 0)
        {
            return;
        }
        int secs = RecordChoices[ComboRecI.SelectedIndex];
        AppState.PostModifyConfig(c => c.HistoryPollIntervalSecs = (ulong)secs);
    }

    private void ComboLang_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || ComboLang.SelectedIndex < 0)
        {
            return;
        }
        string lang = ComboLang.SelectedIndex switch
        {
            1 => "en",
            2 => "zh",
            _ => "auto",
        };
        // Persist + apply: I18n.SetSetting raises LanguageChanged, which
        // re-localizes open windows and refreshes the tray tooltip.
        AppState.PostModifyConfig(c => c.Language = lang);
        I18n.SetSetting(lang switch
        {
            "en" => LanguageSetting.En,
            "zh" => LanguageSetting.Zh,
            _ => LanguageSetting.Auto,
        });
        // Re-sync this page (labels + programmatic selection) after the switch.
        Localize();
    }

    private void SwitchAutostart_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress)
        {
            return;
        }
        bool on = SwitchAutostart.IsOn;
        AppState.PostModifyConfig(c =>
        {
            c.RunAtStartup = on;
            ConfigService.ApplyAutostart(on);
        });
    }
}
