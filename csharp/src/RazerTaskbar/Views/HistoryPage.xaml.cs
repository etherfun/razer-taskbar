//! Port of src/viewer.rs as a WinUI3 page: device picker, range selector,
//! four stat cards, the battery chart, and the cycles/sessions list.
//! Data flows straight from HistoryService (same process, thread-safe reads).

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RazerTaskbar.Core;

namespace RazerTaskbar.Views;

/// <summary>One list row (viewer.rs ListItem).</summary>
public sealed class CycleItem
{
    public bool Charge { get; init; }
    public string Start { get; init; } = "";
    public string Dur { get; init; } = "";
    public string Levels { get; init; } = "";

    public string PillText() => I18n.Tr(Charge ? "CHARGE" : "USE");

    public SolidColorBrush PillBackground() => Solid(Charge ? 0x2A4026 : 0x2B2B2B);

    public SolidColorBrush PillBorderBrush() => Solid(Charge ? 0x6CCB5F : 0x5A5A5A);

    public SolidColorBrush PillForeground() => Solid(Charge ? 0x6CCB5F : 0xB0B0B0);

    public SolidColorBrush DurBrush() => Solid(Charge ? 0x6CCB5F : 0xE8E8E8);

    private static SolidColorBrush Solid(long rgb)
    {
        var c = new Windows.UI.Color { A = 0xFF, R = (byte)(rgb >> 16), G = (byte)(rgb >> 8), B = (byte)rgb };
        return new SolidColorBrush(c);
    }
}

public sealed partial class HistoryPage : Page
{
    private sealed record DeviceEntry(string Handle, string Name);

    private long _rangeDays = 30;
    private List<DeviceEntry> _devices = new();

    /// <summary>Programmatic ItemsSource/SelectedIndex assignment fires
    /// SelectionChanged synchronously; a re-entrant Reload would mutate the
    /// collection while the first modification is still in progress (WinUI
    /// COMException). Guard mirrors SettingsPage._suppress.</summary>
    private bool _suppressSelection;
    private List<Sample> _currentSamples = new();
    /// <summary>Compare-series device ("" = none) for the chart overlay.</summary>
    private string _compareHandle = "";

    public HistoryPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
        Localize();
    }

    /// <summary>Re-localize every label (language switch) and reload the data.</summary>
    public void Localize()
    {
        Range7.Content = I18n.Tr("7 days");
        Range30.Content = I18n.Tr("30 days");
        RangeAll.Content = I18n.Tr("All");
        ExportButtonText.Text = I18n.Tr("Export");
        ChartHeader.Text = I18n.Tr("Battery level");
        LegendCharging.Text = I18n.Tr("charging");
        LegendDischarging.Text = I18n.Tr("discharging");
        LegendOff.Text = I18n.Tr("off (excluded)");
        // Accessibility names (screen readers announce the row label).
        AutomationProperties.SetName(DeviceCombo, I18n.Tr("Shown device"));
        AutomationProperties.SetName(ExportButton, I18n.Tr("Export"));
        AutomationProperties.SetName(RangeButtons, I18n.Tr("Time range"));
        AutomationProperties.SetName(CompareCombo, I18n.Tr("compare"));
        AutomationProperties.SetName(Chart, I18n.Tr("Battery level"));
        Reload();
    }

    private void Device_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
        {
            return;
        }
        Reload();
    }

    private void Range_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || RangeButtons.SelectedIndex < 0)
        {
            return;
        }
        _rangeDays = RangeButtons.SelectedIndex switch
        {
            0 => 7,
            1 => 30,
            _ => 0, // All
        };
        Reload();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        ExportInfoBar.IsOpen = false;
        if (_currentSamples.Count == 0)
        {
            ShowExportInfo(InfoBarSeverity.Warning, I18n.Tr("No data to export."));
            return;
        }
        try
        {
            // Desktop (unpackaged) apps must associate pickers with an owner
            // HWND: WindowNative.GetWindowHandle + InitializeWithWindow.
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            if (!App.TryGetMainWindowHandle(out var hwnd))
            {
                return;
            }
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var name = _devices.FirstOrDefault(d => d.Handle == SelectedHandle()).Name ?? "device";
            var safe = string.Join("", name.Split(Path.GetInvalidFileNameChars()));
            picker.SuggestedFileName = $"razer-battery-{safe}-{DateTime.Now:yyyyMMdd-HHmm}";
            picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
            picker.DefaultFileExtension = ".csv";
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return; // user cancelled
            }
            File.WriteAllText(file.Path, ExportService.ToCsv(_currentSamples));
            ShowExportInfo(InfoBarSeverity.Success, I18n.Tr("CSV exported ({})").Replace("{}", $"{_currentSamples.Count}"));
        }
        catch (Exception ex)
        {
            Log.Error("export failed", ex);
            ShowExportInfo(InfoBarSeverity.Error, I18n.Tr("Export failed"));
        }
    }

    private void ShowExportInfo(InfoBarSeverity severity, string message)
    {
        ExportInfoBar.Severity = severity;
        ExportInfoBar.Message = message;
        ExportInfoBar.IsOpen = true;
    }

    private void Compare_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
        {
            return;
        }
        if (CompareCombo.SelectedIndex <= 0)
        {
            _compareHandle = "";
            Reload();
            return;
        }
        // Index > 0 maps onto the roster order used to build the items
        // (None + all devices except the main selection).
        var others = _devices.Where(d => d.Handle != SelectedHandle()).ToList();
        var idx = CompareCombo.SelectedIndex - 1;
        _compareHandle = idx < others.Count ? others[idx].Handle : "";
        Reload();
    }

    private string SelectedHandle()
    {
        if (DeviceCombo.SelectedItem is DeviceEntry entry)
        {
            return entry.Handle;
        }
        return _devices.Count > 0 ? _devices[0].Handle : "";
    }

    private void Reload()
    {
        // Device roster from the recorded history (viewer.rs device picker).
        var roster = HistoryService.ListDevices();
        _devices = roster.Select(r => new DeviceEntry(r.Handle, r.Name)).ToList();
        var selected = SelectedHandle();
        _suppressSelection = true;
        try
        {
            DeviceCombo.ItemsSource = _devices;
            var index = _devices.FindIndex(d => d.Handle == selected);
            if (index >= 0)
            {
                DeviceCombo.SelectedIndex = index;
            }
            else
            {
                DeviceCombo.SelectedIndex = _devices.Count > 0 ? 0 : -1;
            }
        }
        finally
        {
            _suppressSelection = false;
        }

        // Range (viewer.rs: since = now - range_days*86400, 0 = all).
        var handle = SelectedHandle();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long since = _rangeDays > 0 ? now - (_rangeDays * 86400) : 0;
        _currentSamples = handle.Length > 0 ? HistoryService.SamplesInRange(handle, since) : new List<Sample>();
        var samples = _currentSamples;

        // Stat cards (viewer.rs paint values).
        var stats = HistoryService.CycleStatsOf(samples);
        StatCyclesValue.Text = $"{stats.Cycles}";
        StatUseValue.Text = stats.UseHoursPerPct is { } useRate
            ? HistoryService.FormatDuration((long)Math.Round(useRate * 100.0 * 3600.0))
            : "--";
        StatChargeValue.Text = stats.ChargeHoursPerPct is { } chargeRate
            ? HistoryService.FormatDuration((long)Math.Round(chargeRate * 100.0 * 3600.0))
            : "--";

        var est = handle.Length > 0 ? HistoryService.EstimateFor(handle) : null;
        StatNowValue.Text = est is { } e ? HistoryService.FormatDuration(e.Secs) : "--";
        StatNowValue.Foreground = new SolidColorBrush(
            new Windows.UI.Color { A = 0xFF, R = 0x60, G = 0xCD, B = 0xFF });
        StatNowCaption.Text = I18n.Tr(est is { Charging: true } ? "until full (now)" : "time remaining now");

        // Compare series picker: None + other recorded devices.
        _suppressSelection = true;
        try
        {
            var compareEntries = new List<DeviceEntry> { new("", I18n.Tr("None")) };
            compareEntries.AddRange(_devices.Where(d => d.Handle != handle));
            CompareCombo.ItemsSource = compareEntries.Select(d => d.Name).ToList();
            var cIdx = compareEntries.FindIndex(d => d.Handle == _compareHandle);
            CompareCombo.SelectedIndex = cIdx >= 0 ? cIdx : 0;
        }
        finally
        {
            _suppressSelection = false;
        }
        List<Sample>? compareSamples = null;
        string? compareName = null;
        if (_compareHandle.Length > 0 && _compareHandle != handle)
        {
            compareSamples = HistoryService.SamplesInRange(_compareHandle, since);
            if (compareSamples.Count > 0)
            {
                compareName = _devices.FirstOrDefault(d => d.Handle == _compareHandle)?.Name;
            }
        }

        // Chart.
        Chart.Render(samples, compareSamples, compareName);
        CompareLegend.Visibility = compareSamples is { Count: >= 2 } ? Visibility.Visible : Visibility.Collapsed;
        if (compareName is not null)
        {
            LegendCompare.Text = $"{I18n.Tr("compare")} · {compareName}";
        }

        // Cycle list: discharge + charge spans merged, newest first.
        var (discharge, charge) = HistoryService.ComputeSpans(samples);
        var items = new List<(long EndTs, CycleItem Item)>();
        foreach (var s in discharge)
        {
            items.Add((s.EndTs, new CycleItem
            {
                Charge = false,
                Start = Controls.BatteryChart.FormatStamp(s.StartTs),
                Dur = HistoryService.FormatDuration(s.ActiveSecs),
                Levels = $"{s.LevelStart}→{s.LevelEnd}%",
            }));
        }
        foreach (var s in charge)
        {
            items.Add((s.EndTs, new CycleItem
            {
                Charge = true,
                Start = Controls.BatteryChart.FormatStamp(s.StartTs),
                Dur = HistoryService.FormatDuration(s.ActiveSecs),
                Levels = $"{s.LevelStart}→{s.LevelEnd}%",
            }));
        }
        items.Sort((a, b) => b.EndTs.CompareTo(a.EndTs));
        CycleList.ItemsSource = items.Select(i => i.Item).ToList();
        ListCaption.Text = I18n.Tr("Cycles & charging sessions ({} recorded)").Replace("{}", $"{items.Count}");
    }
}
