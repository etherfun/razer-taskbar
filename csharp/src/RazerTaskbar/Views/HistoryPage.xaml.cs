//! Port of src/viewer.rs as a WinUI3 page: device picker, range selector,
//! four stat cards, the battery chart, and the cycles/sessions list.
//! Data flows straight from HistoryService (same process, thread-safe reads).

using Microsoft.UI.Xaml;
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
        ChartHeader.Text = I18n.Tr("Battery level");
        LegendCharging.Text = I18n.Tr("charging");
        LegendDischarging.Text = I18n.Tr("discharging");
        LegendOff.Text = I18n.Tr("off (excluded)");
        Reload();
    }

    private void Device_SelectionChanged(object sender, SelectionChangedEventArgs e) => Reload();

    private void Range_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RangeButtons.SelectedIndex < 0)
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
        DeviceCombo.ItemsSource = _devices;
        var index = _devices.FindIndex(d => d.Handle == selected);
        if (index >= 0)
        {
            if (DeviceCombo.SelectedIndex != index)
            {
                DeviceCombo.SelectedIndex = index;
            }
        }
        else
        {
            DeviceCombo.SelectedIndex = _devices.Count > 0 ? 0 : -1;
        }

        // Range (viewer.rs: since = now - range_days*86400, 0 = all).
        var handle = SelectedHandle();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long since = _rangeDays > 0 ? now - (_rangeDays * 86400) : 0;
        var samples = handle.Length > 0 ? HistoryService.SamplesInRange(handle, since) : new List<Sample>();

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

        // Chart.
        Chart.Render(samples);

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
