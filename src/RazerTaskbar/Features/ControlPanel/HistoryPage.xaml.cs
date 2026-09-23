//! Port of src/viewer.rs as a WinUI3 page: device picker, range selector,
//! four stat cards, the battery chart, and the cycles/sessions list.
//! Data flows straight from HistoryService (same process, thread-safe reads).

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RazerTaskbar.Core;
using RazerTaskbar.Host;
using RazerTaskbar.Native;

namespace RazerTaskbar.Features.ControlPanel;

/// <summary>One list row (viewer.rs ListItem).</summary>
public sealed class CycleItem
{
    public bool Charge { get; init; }
    /// <summary>Cycle opened by a battery swap rather than by a charge
    /// session (<see cref="Span.SwapStart"/>).</summary>
    public bool Swap { get; init; }
    public string Start { get; init; } = "";
    public string Dur { get; init; } = "";
    public string Levels { get; init; } = "";

    public string PillText() => I18n.Tr(Swap ? "SWAP" : Charge ? "CHARGE" : "USE");

    public SolidColorBrush PillBackground() => Solid(Swap ? 0x3A3524 : Charge ? 0x2A4026 : 0x2B2B2B);

    public SolidColorBrush PillBorderBrush() => Solid(Swap ? 0xC9A227 : Charge ? 0x6CCB5F : 0x5A5A5A);

    public SolidColorBrush PillForeground() => Solid(Swap ? 0xE3C46A : Charge ? 0x6CCB5F : 0xB0B0B0);

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
    /// <summary>Chart checkbox: shade off/unknown stretches (false hides them).</summary>
    private bool _showOff = true;

    /// <summary>Programmatic ItemsSource/SelectedIndex assignment fires
    /// SelectionChanged synchronously; a re-entrant Reload would mutate the
    /// collection while the first modification is still in progress (WinUI
    /// COMException). Guard mirrors SettingsPage._suppress.</summary>
    private bool _suppressSelection;
    private List<Sample> _currentSamples = new();
    /// <summary>Compare-series device ("" = none) for the chart overlay.</summary>
    private string _compareHandle = "";
    /// <summary>Battery-type setting for the shown device (Auto = model table)
    /// and the types it resolves to for the shown / compare device (see
    /// <see cref="BatteryTypes"/>). Resolved on the UI thread in Reload, read
    /// by the background span pass.</summary>
    private BatteryType _batteryType = BatteryType.Auto;
    private BatteryType _resolvedBatteryType = BatteryType.Rechargeable;
    private BatteryType _resolvedCompareType = BatteryType.Rechargeable;
    /// <summary>Reload generation: a slow background load finishing after a
    /// newer selection change must not render stale data.</summary>
    private int _reloadGen;

    public HistoryPage()
    {
        InitializeComponent();
        // One tree per window, reused across tab switches: creating XAML trees
        // repeatedly leaks native memory/handles in WinUI3 (measured; see
        // docs/agent-architecture.md). Loaded still fires on every re-entry,
        // so the data reload path is unchanged.
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        // Wire the bottom bar's adaptive one-line/stacked switch in code: the
        // XAML compiler chokes (WMC9999) on SizeChanged attributes for it.
        BottomBar.SizeChanged += BottomBar_SizeChanged;
        Loaded += (_, _) => Reload();
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

    /// <summary>Re-localize every label (language switch) and reload the data.</summary>
    public void Localize()
    {
        ExportButtonText.Text = I18n.Tr("Export");
        ChartHeader.Text = I18n.Tr("Battery level");
        LegendCharging.Text = I18n.Tr("charging");
        LegendDischarging.Text = I18n.Tr("discharging");
        LegendOff.Text = I18n.Tr("off");
        CompareLabel.Text = I18n.Tr("compare device");
        BatteryTypeLabel.Text = I18n.Tr("Battery type");
        ToolTipService.SetToolTip(BatteryTypeCombo, I18n.Tr("Which battery the device uses. Auto matches the model against the AA/AAA list openrazer uses (Atheris, Orochi, HyperSpeed models, Pro Click Mini); a replaceable cell never charges, so its level jumps read as battery swaps and charge statistics are skipped."));
        ShowOffCheck.Content = I18n.Tr("Show off periods");
        // Stat card captions (values render in Reload).
        StatCyclesCaption.Text = I18n.Tr("discharge cycles");
        StatUseCaption.Text = I18n.Tr("usable per 100% charge");
        StatChargeCaption.Text = I18n.Tr("per full charge");
        // Battery health card.
        HealthHeader.Text = I18n.Tr("battery health");
        HealthSohCaption.Text = I18n.Tr("estimated capacity");
        HealthFadeCaption.Text = I18n.Tr("fade per month");
        HealthEolCaption.Text = I18n.Tr("est. to 80%");
        HealthTip.Text = I18n.Tr("Capacity estimated from charge speed relative to the earliest recorded sessions — charge current is usage-independent, so charge speed isolates capacity fade. Percentage readings quantize coarsely; values are approximate.");
        HealthSohValue.Foreground = new SolidColorBrush(
            new Windows.UI.Color { A = 0xFF, R = 0x60, G = 0xCD, B = 0xFF });
        // Stat card hover tooltips (longer explanations).
        TipCycles.Text = I18n.Tr("Equivalent full cycles completed within the selected range: total discharged percent divided by 100, so one complete charge-empty-recharge sequence counts as one cycle and partial sessions add up fractionally. Off periods are not counted.");
        TipUse.Text = I18n.Tr("Estimated usable time per 100% of charge, from the discharge cycles in range. Recent cycles count most — weight decays with a 30-day half-life (tracking battery aging and habit changes).");
        TipCharge.Text = I18n.Tr("Estimated time to fully charge the device, from the charge sessions in range.");
        TipNow.Text = I18n.Tr("Estimated usable time right now (discharging) or time until full (charging), anchored at the current level and counting down in real time.");
        // Restore the checkbox against the field without triggering a reload.
        _suppressSelection = true;
        try
        {
            ShowOffCheck.IsChecked = _showOff;
        }
        finally
        {
            _suppressSelection = false;
        }
        // Range combo: rebuild (language switch) and restore the selection
        // without firing a reload — Reload runs once at the end.
        _suppressSelection = true;
        try
        {
            RangeCombo.ItemsSource = new List<string>
            {
                I18n.Tr("7 days"),
                I18n.Tr("30 days"),
                I18n.Tr("All"),
            };
            RangeCombo.SelectedIndex = _rangeDays switch
            {
                7 => 0,
                30 => 1,
                _ => 2,
            };
            // Battery-type combo: same rebuild/restore dance; the current
            // selection is re-applied by Reload below.
            BatteryTypeCombo.ItemsSource = new List<string>
            {
                I18n.Tr("Auto"),
                I18n.Tr("Built-in rechargeable"),
                I18n.Tr("Replaceable battery (AA/AAA)"),
            };
            BatteryTypeCombo.SelectedIndex = _batteryType switch
            {
                BatteryType.Rechargeable => 1,
                BatteryType.Replaceable => 2,
                _ => 0,
            };
        }
        finally
        {
            _suppressSelection = false;
        }
        // Accessibility names (screen readers announce the row label).
        AutomationProperties.SetName(DeviceCombo, I18n.Tr("Shown device"));
        AutomationProperties.SetName(ExportButton, I18n.Tr("Export"));
        AutomationProperties.SetName(RangeCombo, I18n.Tr("Time range"));
        AutomationProperties.SetName(BatteryTypeCombo, I18n.Tr("Battery type"));
        AutomationProperties.SetName(CompareCombo, I18n.Tr("compare"));
        AutomationProperties.SetName(ShowOffCheck, I18n.Tr("Show off periods"));
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
        if (_suppressSelection || RangeCombo.SelectedIndex < 0)
        {
            return;
        }
        _rangeDays = RangeCombo.SelectedIndex switch
        {
            0 => 7,
            1 => 30,
            _ => 0, // All
        };
        Reload();
    }

    private void ShowOff_Changed(object sender, RoutedEventArgs e)
    {
        _showOff = ShowOffCheck.IsChecked == true;
        if (_suppressSelection)
        {
            return;
        }
        Reload();
    }

    /// <summary>Battery-type setting changed for the shown device: persist the
    /// per-device override (Auto drops it, so the model table decides again)
    /// and re-interpret the recorded series.</summary>
    private void BatteryType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
        {
            return;
        }
        _batteryType = BatteryTypeCombo.SelectedIndex switch
        {
            1 => BatteryType.Rechargeable,
            2 => BatteryType.Replaceable,
            _ => BatteryType.Auto,
        };
        var handle = SelectedHandle();
        if (handle.Length > 0)
        {
            var picked = _batteryType;
            // The authoritative config copy lives on the widget thread.
            AppState.PostModifyConfig(cfg =>
            {
                if (picked == BatteryType.Auto)
                {
                    cfg.DeviceBatteryTypes.Remove(handle);
                }
                else
                {
                    cfg.DeviceBatteryTypes[handle] = picked.ToConfig();
                }
            });
        }
        // Pass the pick through: the posted config edit has not reached the
        // published snapshot yet, so a plain Reload() would read the old type
        // and snap the combo back (the "click it twice" bug).
        Reload(_batteryType);
    }

    private void BottomBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Bottom controls: one line with a "|" divider when the card is wide
        // enough for both groups; stacked rows without it when narrow.
        bool wide = e.NewSize.Width >= 520;
        BottomDivider.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRow(ShowOffCheck, wide ? 0 : 1);
        Grid.SetColumn(ShowOffCheck, wide ? 2 : 0);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        ExportInfoBar.IsOpen = false;
        // Capture: a background Reload may swap _currentSamples mid-export.
        var samples = _currentSamples;
        if (samples.Count == 0)
        {
            ShowExportInfo(InfoBarSeverity.Warning, I18n.Tr("No data to export"));
            return;
        }
        try
        {
            // Desktop (unpackaged) apps must associate pickers with an owner
            // HWND: WindowNative.GetWindowHandle + InitializeWithWindow.
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            if (!AppHost.TryGetControlPanelHandle(out var hwnd))
            {
                return;
            }
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var name = _devices.FirstOrDefault(d => d.Handle == SelectedHandle())?.Name ?? "device";
            var safe = string.Join("", name.Split(Path.GetInvalidFileNameChars()));
            picker.SuggestedFileName = $"razer-battery-{safe}-{DateTime.Now:yyyyMMdd-HHmm}";
            picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
            picker.DefaultFileExtension = ".csv";
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return; // user cancelled
            }
            // "All" ranges can be megabytes: build + write off the UI thread.
            var csv = await Task.Run(() => ExportService.ToCsv(samples));
            await File.WriteAllTextAsync(file.Path, csv);
            // The awaits above resumed on a thread-pool thread (no UI
            // SynchronizationContext in WinUI 3): hop back for the InfoBar.
            DispatcherQueue.TryEnqueue(() =>
                ShowExportInfo(InfoBarSeverity.Success, I18n.Tr("CSV exported ({})").Replace("{}", $"{samples.Count}")));
        }
        catch (Exception ex)
        {
            Log.Error("export failed", ex);
            DispatcherQueue.TryEnqueue(
                () => ShowExportInfo(InfoBarSeverity.Error, I18n.Tr("Export failed")));
        }
    }

    private void ShowExportInfo(InfoBarSeverity severity, string message)
    {
        ExportInfoBar.Severity = severity;
        ExportInfoBar.Message = message;
        ExportInfoBar.IsOpen = true;
    }

    /// <summary>"14 mo"-style remaining-lifespan text (localized unit),
    /// capped so a near-flat fade trend doesn't print absurd horizons.</summary>
    private static string FormatMonths(double months)
    {
        if (months < 1)
        {
            return I18n.Tr("<1 mo");
        }
        var n = (long)Math.Round(months);
        return I18n.Tr("{} mo").Replace("{}", n >= 120 ? "120+" : $"{n}");
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

    private async void Reload(BatteryType? batteryTypeOverride = null)
    {
        int gen = ++_reloadGen;

        // Device roster from the recorded history (viewer.rs device picker).
        var roster = HistoryService.ListDevices();
        // Same-name devices (two units, or a split HID/log identity) get a
        // serial tag: "Razer Mouse(31000044)".
        var rosterNames = roster.Select(r => r.Name).ToList();
        _devices = roster.Select(r => new DeviceEntry(
            r.Handle, DeviceLabels.Label(r.Name, r.Handle, rosterNames))).ToList();
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
        // Battery type of the shown device: the per-device override from
        // settings, else the openrazer-derived model table (BatteryTypes).
        // Resolved on this (UI) thread — the background span pass below needs
        // the answer, and the config snapshot is a lock-free read. The config
        // edit itself is posted to the widget thread, so the published
        // snapshot lags a beat behind a just-picked type; the picker passes
        // its value in (batteryTypeOverride) instead of re-reading it here,
        // otherwise Reload would dial the combo straight back to the old one.
        var typeMap = AppState.Instance.ConfigSnapshot().DeviceBatteryTypes;
        _batteryType = batteryTypeOverride ?? BatteryTypes.Parse(
            handle.Length > 0 && typeMap.TryGetValue(handle, out var configured) ? configured : null);
        _resolvedBatteryType = BatteryTypes.Resolve(
            _batteryType, roster.FirstOrDefault(r => r.Handle == handle).Name ?? "");
        _suppressSelection = true;
        try
        {
            BatteryTypeCombo.SelectedIndex = _batteryType switch
            {
                BatteryType.Rechargeable => 1,
                BatteryType.Replaceable => 2,
                _ => 0,
            };
        }
        finally
        {
            _suppressSelection = false;
        }
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long since = _rangeDays > 0 ? now - (_rangeDays * 86400) : 0;
        var requestedCompare = _compareHandle;
        _resolvedCompareType = BatteryTypes.Resolve(
            BatteryTypes.Parse(typeMap.TryGetValue(requestedCompare, out var compareCfg) ? compareCfg : null),
            roster.FirstOrDefault(r => r.Handle == requestedCompare).Name ?? "");

        // Reads and statistics off the UI thread: an "All" range pulls the
        // whole series and one Deflate+ComputeSpans pass feeds the stat
        // cards, health card and cycle list (three full recomputes used to
        // run here, all on the UI thread).
        (List<Sample>, List<Span>, List<Span>, CycleStats, HealthStats?, List<Sample>?, Estimate?) data;
        try
        {
            data = await Task.Run(() =>
            {
                var samples = handle.Length > 0
                    ? HistoryService.SamplesInRange(handle, since)
                    : new List<Sample>();
                // A replaceable cell has no charging hardware: drop whatever
                // charge flag the reading carried, so neither a bogus status
                // nor the swap jump can be read as a charge session.
                bool replaceable = _resolvedBatteryType == BatteryType.Replaceable;
                if (replaceable && samples.Count > 0)
                {
                    samples = BatteryTypes.AsReplaceable(samples);
                }
                var (discharge, charge) = HistoryService.ComputeSpans(ReboundFilter.Deflate(samples));
                var stats = HistoryService.CycleStatsOfSpans(discharge, charge);
                // No charge sessions → no charge-speed signal → no fade
                // estimate worth showing (the card says so instead).
                var health = replaceable ? null : HistoryService.HealthStatsOfSpans(charge);
                List<Sample>? compareSamples = null;
                if (requestedCompare.Length > 0 && requestedCompare != handle)
                {
                    compareSamples = HistoryService.SamplesInRange(requestedCompare, since);
                    if (_resolvedCompareType == BatteryType.Replaceable && compareSamples.Count > 0)
                    {
                        compareSamples = BatteryTypes.AsReplaceable(compareSamples);
                    }
                }
                // Remaining time for a replaceable cell is a pure discharge
                // prediction off the corrected series (the cached estimate
                // would still count the noise charge flag).
                Estimate? est = null;
                if (handle.Length > 0)
                {
                    est = replaceable
                        ? (samples.Count > 0 ? HistoryService.Predict(samples, samples[^1].Level, false) : null)
                        : HistoryService.EstimateFor(handle);
                }
                return (samples, discharge, charge, stats, health, compareSamples, est);
            });
        }
        catch (Exception e)
        {
            Log.Error("history load failed", e);
            return;
        }
        // WinUI 3's UI thread has no SynchronizationContext: the continuation
        // after the await above resumes on a thread-pool thread, and any XAML
        // touch there throws RPC_E_WRONG_THREAD (0x8001010E), killing the
        // process — hop back to the dispatcher before touching controls.
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                ApplyReload(gen, handle, data);
            }
            catch (Exception e)
            {
                Log.Error("history render failed", e);
            }
        });
    }

    private void ApplyReload(
        int gen,
        string handle,
        (List<Sample> samples,
         List<Span> discharge,
         List<Span> charge,
         CycleStats stats,
         HealthStats? health,
         List<Sample>? compareSamples,
         Estimate? est) data)
    {
        if (gen != _reloadGen)
        {
            return; // superseded: a newer selection changed the data set
        }

        var samples2 = data.samples;
        _currentSamples = samples2;
        var stats2 = data.stats;

        // Stat cards (viewer.rs paint values). Cycles are equivalent full
        // cycles (a double): 1–2 decimals so fractional accrual stays visible.
        StatCyclesValue.Text = $"{stats2.Cycles:0.0#}";
        StatUseValue.Text = stats2.UseHoursPerPct is { } useRate
            ? HistoryService.FormatDuration((long)Math.Round(useRate * 100.0 * 3600.0))
            : "--";
        StatChargeValue.Text = stats2.ChargeHoursPerPct is { } chargeRate
            ? HistoryService.FormatDuration((long)Math.Round(chargeRate * 100.0 * 3600.0))
            : "--";

        var est2 = data.est;
        StatNowValue.Text = est2 is { } e ? HistoryService.FormatDuration(e.Secs) : "--";
        StatNowValue.Foreground = new SolidColorBrush(
            new Windows.UI.Color { A = 0xFF, R = 0x60, G = 0xCD, B = 0xFF });
        StatNowCaption.Text = I18n.Tr(est2 is { Charging: true } ? "until full (now)" : "time remaining now");

        // Battery health (computed over ALL recorded data, not range-limited:
        // fade is a years-scale trend). A replaceable cell cannot have one —
        // the estimate reads charge speed, which only a charging pack shows.
        if (_resolvedBatteryType == BatteryType.Replaceable)
        {
            HealthSohValue.Text = I18n.Tr("n/a (replaceable battery)");
            HealthFadeValue.Text = "--";
            HealthEolValue.Text = "--";
        }
        else if (data.health is { } h)
        {
            HealthSohValue.Text = $"≈{Math.Round(h.SohPct)}%";
            HealthFadeValue.Text = h.FadePerMonthPct >= 0.1 ? $"−{h.FadePerMonthPct:0.0}%" : I18n.Tr("stable");
            HealthEolValue.Text = h.MonthsToEol switch
            {
                0 => I18n.Tr("at 80% now"),
                null => "--",
                { } months => I18n.Tr("to 80% in {}").Replace("{}", FormatMonths(months)),
            };
        }
        else
        {
            HealthSohValue.Text = I18n.Tr("insufficient data");
            HealthFadeValue.Text = "--";
            HealthEolValue.Text = "--";
        }

        // Compare series picker: None + other recorded devices.
        _suppressSelection = true;
        try
        {
            var compareEntries = new List<DeviceEntry> { new("", I18n.Tr("None")) };
            compareEntries.AddRange(_devices.Where(d => d.Handle != handle));
            CompareCombo.ItemsSource = compareEntries.Select(d => d.Name).ToList();
            var cIdx = compareEntries.FindIndex(d => d.Handle == _compareHandle);
            CompareCombo.SelectedIndex = cIdx >= 0 ? cIdx : 0;
            // A stale handle (the compare device became the main device,
            // or vanished) resets to None — combo, state and chart must
            // always agree.
            if (cIdx < 0)
            {
                _compareHandle = "";
            }
        }
        finally
        {
            _suppressSelection = false;
        }
        // The prefetch used the handle as selected before the reset above —
        // a reset (stale compare device) drops it, exactly like the old
        // fetch-after-reset ordering.
        List<Sample>? compareSamples2 = _compareHandle.Length > 0 ? data.compareSamples : null;
        string? compareName = null;
        if (compareSamples2 is { Count: > 0 })
        {
            compareName = _devices.FirstOrDefault(d => d.Handle == _compareHandle)?.Name;
        }

        // Chart.
        Chart.Render(samples2, compareSamples2, compareName, _showOff);
        CompareLegend.Visibility = compareSamples2 is { Count: >= 2 } ? Visibility.Visible : Visibility.Collapsed;
        if (compareName is not null)
        {
            LegendCompare.Text = compareName;
        }

        // Cycle list: discharge + charge spans merged, newest first (the
        // spans came from the single deflated pass above).
        var items = new List<(long EndTs, CycleItem Item)>();
        foreach (var s in data.discharge)
        {
            items.Add((s.EndTs, new CycleItem
            {
                Charge = false,
                // A cycle the device started on a freshly inserted cell reads
                // as a swap, not as a charge session (see Span.SwapStart).
                Swap = s.SwapStart,
                Start = $"{BatteryChart.FormatStamp(s.StartTs)} → {BatteryChart.FormatStamp(s.EndTs)}",
                Dur = HistoryService.FormatDuration(s.ActiveSecs),
                Levels = $"{s.LevelStart}→{s.LevelEnd}%",
            }));
        }
        foreach (var s in data.charge)
        {
            items.Add((s.EndTs, new CycleItem
            {
                Charge = true,
                Start = $"{BatteryChart.FormatStamp(s.StartTs)} → {BatteryChart.FormatStamp(s.EndTs)}",
                Dur = HistoryService.FormatDuration(s.ActiveSecs),
                Levels = $"{s.LevelStart}→{s.LevelEnd}%",
            }));
        }
        items.Sort((a, b) => b.EndTs.CompareTo(a.EndTs));
        CycleList.ItemsSource = items.Select(i => i.Item).ToList();
        ListCaption.Text = I18n.Tr("Cycles & charging sessions ({} recorded)").Replace("{}", $"{items.Count}");
    }
}
