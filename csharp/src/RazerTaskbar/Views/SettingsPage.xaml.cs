//! Port of src/settings.rs as a WinUI3 page following the Win11 settings
//! conventions (section headers + title/description rows + right-aligned
//! controls). The apply-immediately model is preserved: every handler edits
//! the authoritative config on the widget thread and persists settings.json;
//! the watcher and other consumers pick changes up on their own cadence.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RazerTaskbar.Core;
using RazerTaskbar.Native;

namespace RazerTaskbar.Views;

public sealed partial class SettingsPage : Page
{
    private static readonly int[] PollChoices = [5, 10, 15, 30, 60];
    private static readonly int[] RecordChoices = [1, 2, 5, 10, 30];

    /// <summary>Programmatic ComboBox SelectionChanged needs an echo guard
    /// during init/refresh (Win32 had none because BM_SETCHECK doesn't fire).</summary>
    private bool _suppress;

    public SettingsPage()
    {
        InitializeComponent();
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
            LblShownDevice.Text = I18n.Tr("Shown device");
            DescShownDevice.Text = "Which device's battery the widget displays.";
            LblSide.Text = I18n.Tr("Widget side");
            DescSide.Text = "Anchor the widget on the left or right side of the taskbar.";
            LblEst.Text = I18n.Tr("Show time remaining on widget");
            DescEst.Text = "Second row with the predicted remaining / time-to-full duration.";
            LblTray.Text = I18n.Tr("Show tray icon");
            DescTray.Text = "Notification-area icon with tooltip and menu.";
            LblHover.Text = I18n.Tr("Show devices on hover");
            DescHover.Text = "Hover the widget to list every known device.";
            LblPoll.Text = I18n.Tr("Poll interval");
            DescPoll.Text = "Display refresh cadence (seconds) when there is no log activity.";
            LblRec.Text = I18n.Tr("Record battery history");
            DescRec.Text = "Sample battery.db and predict usage time.";
            LblRecI.Text = I18n.Tr("Record interval");
            DescRecI.Text = "Sampling cadence while recording is enabled (seconds).";
            LblLang.Text = I18n.Tr("Language");
            DescLang.Text = "UI language. Auto follows the Windows UI language.";
            LblAutostart.Text = I18n.Tr("Run at startup");
            DescAutostart.Text = "Register HKCU\\...\\Run\\RazerTaskbar.";
            SideLeft.Content = I18n.Tr("Left");
            SideRight.Content = I18n.Tr("Right");

            // Shown device (0 = All devices, then connected sorted by name).
            var devices = AppState.Instance.Devices.Snapshot();
            var connected = devices.Values
                .Where(d => d.IsConnected)
                .OrderBy(d => d.Name, StringComparer.Ordinal)
                .ToList();
            var entries = new List<(string Handle, string Label)> { ("", I18n.Tr("All devices")) };
            entries.AddRange(connected.Select(d =>
                (d.Handle, $"{d.Name} — {d.BatteryPercentage}%")));
            ComboDevice.ItemsSource = entries.Select(e => e.Label).ToList();
            ComboDevice.SelectedIndex = 0;
            var shown = cfg.ShownDeviceHandle;
            var idx = entries.FindIndex(e => e.Handle == shown);
            if (idx >= 0)
            {
                ComboDevice.SelectedIndex = idx;
            }

            // Widget side.
            SideButtons.SelectedIndex = cfg.WidgetSide == "left" ? 0 : 1;

            // Toggles.
            SwitchEst.IsOn = cfg.ShowEstimatedTime;
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
        // Entry 0 = All devices (""); otherwise the connected roster order
        // matches the label list built in Localize.
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

    private void Side_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || SideButtons.SelectedIndex < 0)
        {
            return;
        }
        string side = SideButtons.SelectedIndex == 0 ? "left" : "right";
        AppState.PostModifyConfig(c => c.WidgetSide = side);
        AppState.PostReposition();
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
