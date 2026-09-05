// Port of src/history.rs #[cfg(test)] tests (spans, weighting, prediction).

using RazerTaskbar.Core;
using Xunit;

namespace RazerTaskbar.Tests;

[Collection("I18nSequential")]
public sealed class HistoryTests
{
    private static Sample S(long ts, int level, bool charging, bool connected)
        => new(ts, level, charging, connected);

    [Fact]
    public void DurationFormatting()
    {
        Assert.Equal("<1m", HistoryService.FormatDuration(0));
        Assert.Equal("<1m", HistoryService.FormatDuration(30));
        Assert.Equal("45m", HistoryService.FormatDuration(45 * 60));
        Assert.Equal("3h25m", HistoryService.FormatDuration(3 * 3600 + 25 * 60));
        Assert.Equal("3h", HistoryService.FormatDuration(3 * 3600));
        Assert.Equal("25d4h", HistoryService.FormatDuration(25 * 86400 + 4 * 3600));
    }

    [Fact]
    public void EstimateText()
    {
        // format_estimate_verbose is localized: pin English (serialized with
        // the i18n test via the I18nSequential collection — the language is
        // global state).
        I18n.SetSetting(LanguageSetting.En);
        var e = new Estimate(3 * 3600 + 25 * 60, Charging: false);
        Assert.Equal("~3h25m", HistoryService.FormatEstimateCompact(e));
        Assert.Equal("~3h25m left", HistoryService.FormatEstimateVerbose(e));
        var c = new Estimate(70 * 60, Charging: true);
        Assert.Equal("+1h10m", HistoryService.FormatEstimateCompact(c));
        Assert.Equal("full in 1h10m", HistoryService.FormatEstimateVerbose(c));
        I18n.SetSetting(LanguageSetting.Auto);
    }

    [Fact]
    public void DischargeThenChargeSplit()
    {
        // 900s steps = the 15-min heartbeat cadence, well under the 30-min gap break.
        List<Sample> samples =
        [
            S(0, 100, false, true),
            S(900, 95, false, true),
            S(1800, 90, false, true),
            S(1801, 90, true, true),
            S(2701, 95, true, true),
        ];
        var (dis, chg) = HistoryService.ComputeSpans(samples);
        Assert.Single(dis);
        var d = dis[0];
        Assert.Equal((0L, 1800L), (d.StartTs, d.EndTs));
        Assert.Equal(1800, d.ActiveSecs);
        Assert.Equal((100, 90), (d.LevelStart, d.LevelEnd));
        Assert.Equal(10, d.MovedPct);
        Assert.Single(chg);
        var c = chg[0];
        // The boundary interval (1800→1801) belongs to neither run.
        Assert.Equal((1801L, 2701L), (c.StartTs, c.EndTs));
        Assert.Equal(900, c.ActiveSecs);
        Assert.Equal(5, c.MovedPct);
    }

    [Fact]
    public void PowerSaveGapExcludedButCycleContinues()
    {
        // Device auto-offs at 901 (connected=0), wakes at 1801. The off
        // stretch counts no active time yet the cycle stays one span.
        List<Sample> samples =
        [
            S(0, 100, false, true),
            S(900, 98, false, true),
            S(901, 98, false, false),
            S(1800, 97, false, false),
            S(1801, 97, false, true),
            S(2700, 95, false, true),
        ];
        var (dis, _) = HistoryService.ComputeSpans(samples);
        Assert.Single(dis);
        var d = dis[0];
        Assert.Equal((0L, 2700L), (d.StartTs, d.EndTs));
        // 0→900 on + 1800→1801 (wake tick) + 1801→2700 on; the off stretch
        // contributes nothing.
        Assert.Equal(900 + 1 + 899, d.ActiveSecs);
        Assert.Equal(4, d.MovedPct);
        Assert.Equal((100, 95), (d.LevelStart, d.LevelEnd));
    }

    [Fact]
    public void SilentGapNotCountedAsActive()
    {
        // No samples at all for >30 min (PC slept): time and the level move
        // across the gap are both excluded.
        List<Sample> samples =
        [
            S(0, 100, false, true),
            S(900, 98, false, true),
            S(900 + 3600, 97, false, true),
            S(900 + 3600 + 900, 95, false, true),
        ];
        var (dis, _) = HistoryService.ComputeSpans(samples);
        Assert.Single(dis);
        // Only the two short intervals count: 900 + 900, 2% + 2%.
        Assert.Equal(1800, dis[0].ActiveSecs);
        Assert.Equal(4, dis[0].MovedPct);
    }

    [Fact]
    public void BatterySwapJumpEndsCycle()
    {
        // Swappable-battery device: 15% → 80% while discharging closes the
        // cycle and starts a fresh one at the new level.
        List<Sample> samples =
        [
            S(0, 20, false, true),
            S(900, 15, false, true),
            S(1800, 80, false, true),
            S(2700, 75, false, true),
        ];
        var (dis, _) = HistoryService.ComputeSpans(samples);
        Assert.Equal(2, dis.Count);
        Assert.Equal((0L, 900L), (dis[0].StartTs, dis[0].EndTs));
        Assert.Equal((20, 15), (dis[0].LevelStart, dis[0].LevelEnd));
        Assert.Equal(5, dis[0].MovedPct);
        Assert.Equal((1800L, 2700L), (dis[1].StartTs, dis[1].EndTs));
        Assert.Equal((80, 75), (dis[1].LevelStart, dis[1].LevelEnd));
    }

    [Fact]
    public void JumpThatFallsBackIsAGlitch()
    {
        // 48 → 100 → 47 one step later: the jump sample is dropped and the
        // run continues as if it never happened.
        List<Sample> samples =
        [
            S(0, 50, false, true),
            S(900, 48, false, true),
            S(1800, 100, false, true),
            S(1801, 47, false, true),
        ];
        var (dis, _) = HistoryService.ComputeSpans(samples);
        Assert.Single(dis);
        var d = dis[0];
        Assert.Equal((50, 47), (d.LevelStart, d.LevelEnd));
        // 0→900 counted; the glitch step is skipped, then 900→1801 counts.
        Assert.Equal(900 + 901, d.ActiveSecs);
        Assert.Equal(3, d.MovedPct);
    }

    [Fact]
    public void EmptyBatteryEndsCycle()
    {
        List<Sample> samples =
        [
            S(0, 100, false, true),
            S(900, 50, false, true),
            S(901, 0, false, true),
            S(1800, 0, false, true),
        ];
        var (dis, _) = HistoryService.ComputeSpans(samples);
        Assert.Equal(2, dis.Count);
        Assert.Equal((901L, 0, 100), (dis[0].EndTs, dis[0].LevelEnd, dis[0].MovedPct));
        // Trailing zero-level run is a degenerate span (filtered by weight).
        Assert.Equal(0, dis[1].MovedPct);
    }

    [Fact]
    public void OpenCycleKeptAtSeriesEnd()
    {
        List<Sample> samples = [S(0, 100, false, true), S(900, 90, false, true)];
        var (dis, _) = HistoryService.ComputeSpans(samples);
        Assert.Single(dis);
        Assert.Equal(900, dis[0].ActiveSecs);
    }

    [Fact]
    public void ChargeAcrossSilentGapDoesNotInflateRate()
    {
        // Dock charging overnight: 5% in 15 min counted, the 10h silent gap
        // adds neither time nor percent.
        List<Sample> samples =
        [
            S(0, 50, true, true),
            S(900, 55, true, true),
            S(900 + 36000, 100, true, true),
            S(900 + 36000 + 1, 100, false, true),
        ];
        var (_, chg) = HistoryService.ComputeSpans(samples);
        Assert.Single(chg);
        Assert.Equal(900, chg[0].ActiveSecs);
        Assert.Equal(5, chg[0].MovedPct);
    }

    [Fact]
    public void WeightingRecentFullExtendedHalfOldIgnored()
    {
        static Span Span(long end, long active, int moved)
            => new(end - active, end, active, 100, 100 - moved, moved);

        // 10 recent cycles: 1h per 10% (rate 0.1 h/%); 2 older ones are much
        // worse (5h per 5%) and must only count at half weight.
        var spans = Enumerable.Range(0, 10).Select(i => Span(1000 + i, 3600, 10)).ToList();
        spans.Add(Span(900, 5 * 3600, 5));
        spans.Add(Span(800, 5 * 3600, 5));
        // w_hours = 10*1 + 0.5*(5+5) = 15; w_pct = 10*1 + 0.5*(5+5) = 105.
        var rate = HistoryService.WeightedHoursPerPct(spans)!.Value;
        Assert.Equal(15.0 / 105.0, rate, 9);

        // Beyond EXTENDED_LIMIT the oldest spans drop out entirely.
        var many = Enumerable.Range(0, 10).Select(i => Span(2000 + i, 3600, 10)).ToList();
        many.AddRange(Enumerable.Range(0, 95).Select(i => Span(1000 + i, 3600, 10)));
        many.Add(Span(100, 10 * 3600, 10)); // oldest, must be ignored
        // 100 counted spans * 1h / (100 * 10%) = 0.1 h/%.
        var rate2 = HistoryService.WeightedHoursPerPct(many)!.Value;
        Assert.Equal(0.1, rate2, 9);

        // Degenerate spans (tiny move / too short) never contribute.
        List<Span> junk =
        [
            Span(500, 30, 1),  // too short
            Span(600, 120, 2), // move below floor
            Span(700, 6 * 3600, 10),
        ];
        var rate3 = HistoryService.WeightedHoursPerPct(junk)!.Value;
        Assert.Equal(0.6, rate3, 9);
        Assert.Null(HistoryService.WeightedHoursPerPct([Span(500, 30, 1)]));
    }

    [Fact]
    public void PredictCycleBasedAndCharge()
    {
        // 2.5h active for 50% drain → 0.05 h/% → 50% left = 2.5h.
        var samples = new List<Sample>();
        for (var i = 0; i <= 10; i++)
        {
            samples.Add(S(i * 900, 100 - i * 5, false, true));
        }
        samples.Add(S(10 * 900 + 1, 50, true, true));
        samples.Add(S(10 * 900 + 901, 100, true, true));
        samples.Add(S(10 * 900 + 902, 100, false, true));
        var e = HistoryService.Predict(samples, 50, false)!.Value;
        Assert.InRange(e.Secs, 9000 - 60, 9000 + 60);
        Assert.False(e.Charging);
        // Charged back 50→100 in 900s → 18s/% → from 80%: 360s to full.
        var e2 = HistoryService.Predict(samples, 80, true)!.Value;
        Assert.InRange(e2.Secs, 360 - 60, 360 + 60);
        Assert.True(e2.Charging);
    }

    [Fact]
    public void PredictFallsBackToInstantRate()
    {
        // Only 30 min of history: no qualifying cycle, instant rate kicks in.
        List<Sample> samples =
        [
            S(0, 80, false, true),
            S(600, 79, false, true),
            S(1200, 78, false, true),
            S(1800, 78, false, true),
        ];
        // 1200s active for 2% → 1/6 h/% → 78% left = 46800s.
        var e = HistoryService.Predict(samples, 78, false)!.Value;
        Assert.False(e.Charging);
        Assert.InRange(e.Secs, 46800 - 60, 46800 + 60);
        // Charging: 20% gained in 30 min → 0.025 h/% → 90%→full = 900s.
        List<Sample> chg =
        [
            S(0, 70, true, true),
            S(600, 76, true, true),
            S(1200, 88, true, true),
            S(1800, 90, true, true),
        ];
        var e2 = HistoryService.Predict(chg, 90, true)!.Value;
        Assert.True(e2.Charging);
        Assert.InRange(e2.Secs, 900 - 60, 900 + 60);
        // Disconnected device: no prediction at all.
        List<Sample> off =
        [
            S(0, 80, false, true),
            S(600, 79, false, true),
            S(1200, 78, false, false),
        ];
        Assert.Null(HistoryService.Predict(off, 78, false));
    }

    [Fact]
    public void SwapJumpPredictionsStaySane()
    {
        // Two swap-separated cycles with different rates: both are within
        // the newest-10 window and blend at full weight.
        var samples = new List<Sample>();
        // Cycle 1: 30→20 over 5×900s (drop 10, active 4500s).
        for (var i = 0; i <= 5; i++)
        {
            samples.Add(S(i * 900, 30 - i * 2, false, true));
        }
        // Swap: 20 → 80 (jump of 60 closes the cycle).
        samples.Add(S(5 * 900 + 1, 80, false, true));
        // Cycle 2 (newest, open): 80→60 over 5×900s (drop 20, active 4500s).
        for (var i = 1; i <= 5; i++)
        {
            samples.Add(S(5 * 900 + 1 + i * 900, 80 - i * 4, false, true));
        }
        var (dis, _) = HistoryService.ComputeSpans(samples);
        Assert.Equal(2, dis.Count);
        Assert.Equal(10, dis[0].MovedPct);
        Assert.Equal(20, dis[1].MovedPct);
        // Weighted rate = (4501s + 4500s) / 30% ≈ 0.0833 h/% (the 1s swap
        // tick lands in cycle 1).
        var rate = HistoryService.WeightedHoursPerPct(dis)!.Value;
        Assert.Equal(2.5 / 30.0, rate, 3);
    }
}
