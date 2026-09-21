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
        try
        {
            var e = new Estimate(3 * 3600 + 25 * 60, Charging: false);
            Assert.Equal("~3h25m", HistoryService.FormatEstimateCompact(e));
            Assert.Equal("~3h25m left", HistoryService.FormatEstimateVerbose(e));
            var c = new Estimate(70 * 60, Charging: true);
            Assert.Equal("+1h10m", HistoryService.FormatEstimateCompact(c));
            Assert.Equal("full in 1h10m", HistoryService.FormatEstimateVerbose(c));
        }
        finally
        {
            I18n.SetSetting(LanguageSetting.Auto);
        }
    }

    [Fact]
    public void DisconnectedSamplesCarryTrustedLevel()
    {
        // A disconnected device cannot report a live level: the store
        // parrots its last read, which may be the very glitch a spike hold
        // is about to catch (2026-09-10 05:04: 58% live → glitch-100 at
        // power-off → a 5.5h stale plateau committed by the grace expiry).
        // Record the series' trusted level and clear the stale charging
        // flag instead, so the hold sees the true fall-back.
        var trusted = S(0, 58, false, true);
        var coalesced = HistoryService.CoalesceSample(S(900, 100, true, false), trusted);
        Assert.Equal(58, coalesced.Level);
        Assert.False(coalesced.Charging);
        Assert.False(coalesced.Connected);
        // Connected readings and first-sight rows (no trusted history) pass
        // through untouched.
        Assert.Equal(S(901, 100, true, true), HistoryService.CoalesceSample(S(901, 100, true, true), trusted));
        Assert.Equal(S(902, 100, false, false), HistoryService.CoalesceSample(S(902, 100, false, false), null));
    }

    [Fact]
    public void SpikeFilterDropsGlitchSegment()
    {
        // Real glitch from battery.db (2026-09-06, device 632516H31000044):
        // 65% steady, jumped to 100% (not charging) for ~20s, fell back to
        // 67%. The bogus segment must never be written; only the returning
        // sample is.
        var f = new SpikeFilter();
        Assert.Equal(new[] { S(0, 65, false, true) }, f.Admit("d", S(0, 65, false, true), 65));
        Assert.Empty(f.Admit("d", S(240, 100, false, true), 65)); // jump held
        Assert.Empty(f.Admit("d", S(255, 100, false, true), 65)); // still held, deduped
        Assert.Equal(new[] { S(260, 67, false, true) }, f.Admit("d", S(260, 67, false, true), 65));
        // Admission is normal again afterwards.
        Assert.Equal(new[] { S(300, 66, false, true) }, f.Admit("d", S(300, 66, false, true), 67));
    }

    [Fact]
    public void SpikeFilterCommitsConfirmedJump()
    {
        // Swap/reconnect: the level stays away from the pre-jump value past
        // the grace window — the held backlog commits in order and normal
        // admission resumes from the new level.
        var f = new SpikeFilter();
        Assert.Equal(new[] { S(0, 50, false, true) }, f.Admit("a", S(0, 50, false, true), 50));
        Assert.Empty(f.Admit("a", S(5, 100, false, true), 50));
        var commit = f.Admit("a", S(70, 99, false, true), 50);
        Assert.Equal(new[] { S(5, 100, false, true), S(70, 99, false, true) }, commit);
        Assert.Equal(new[] { S(75, 98, false, true) }, f.Admit("a", S(75, 98, false, true), 99));
    }

    [Fact]
    public void SpikeFilterToleranceAndReset()
    {
        // A partial fall-back within tolerance also ends a glitch; small
        // drift is never held; Reset drops an unconfirmed hold (device
        // vanished mid-jump), resuming admission as if nothing happened.
        var f = new SpikeFilter();
        Assert.Equal(new[] { S(0, 50, false, true) }, f.Admit("a", S(0, 50, false, true), 50));
        Assert.Empty(f.Admit("a", S(10, 100, false, true), 50));
        Assert.Equal(new[] { S(20, 52, false, true) }, f.Admit("a", S(20, 52, false, true), 50));
        Assert.Equal(new[] { S(30, 45, false, true) }, f.Admit("a", S(30, 45, false, true), 52));
        Assert.Equal(new[] { S(40, 30, false, true) }, f.Admit("b", S(40, 30, false, true), 30));
        Assert.Empty(f.Admit("b", S(45, 100, false, true), 30));
        f.Reset("b");
        Assert.Equal(new[] { S(80, 100, false, true) }, f.Admit("b", S(80, 100, false, true), 100));
    }

    [Fact]
    public void SpikeFilterDropsReconnectGlitchWithStaleAnchor()
    {
        // Real failure from battery.db (2026-09-08 06:56, Viper
        // 632516H31000044): 8 minutes of disconnected heartbeats at 61%,
        // then the device reconnects through a flapping dongle — first
        // reading 100%, true level 65 two polls later. 65 is outside the
        // ±3 tolerance of the stale anchor 61, so expiry committed the
        // glitch, and its poisoned 100 anchor then admitted the follow-up
        // oscillation. The swing-back rule must drop the segment and trust
        // nothing until the true level re-reports against the clean series.
        var f = new SpikeFilter();
        Assert.Equal(new[] { S(0, 61, false, false) }, f.Admit("v", S(0, 61, false, false), 61));
        Assert.Empty(f.Admit("v", S(471, 100, false, true), 61)); // reconnect, bogus 100 — held
        Assert.Empty(f.Admit("v", S(473, 65, false, true), 61));  // swings 100→65: glitch, trust nothing
        Assert.Equal(new[] { S(474, 65, false, true) }, f.Admit("v", S(474, 65, false, true), 61));
        // A later oscillation against the clean anchor drops via tolerance.
        Assert.Empty(f.Admit("v", S(477, 100, false, true), 65));
        Assert.Equal(new[] { S(478, 65, false, true) }, f.Admit("v", S(478, 65, false, true), 65));
    }

    [Fact]
    public void SpikeFilterHoldsNearFullGlitchBelowJumpPct()
    {
        // 85% → 100% is only a 15-point jump — under JumpPct — but an
        // instantaneous rise while discharging is never real: held, and the
        // fall-back to the fresh anchor drops it. A genuine plug-in of the
        // same size rises with the charging flag set and is not held.
        var f = new SpikeFilter();
        Assert.Equal(new[] { S(0, 85, false, true) }, f.Admit("n", S(0, 85, false, true), 85));
        Assert.Empty(f.Admit("n", S(5, 100, false, true), 85));
        Assert.Equal(new[] { S(10, 82, false, true) }, f.Admit("n", S(10, 82, false, true), 85));
        Assert.Equal(new[] { S(20, 88, true, true) }, f.Admit("n", S(20, 88, true, true), 82));
    }

    [Fact]
    public void SpikeFilterCommitsSmallRealSwapAfterWindow()
    {
        // A rise caught by the rise rule that never falls back is a real
        // battery swap: the held backlog commits when the window expires.
        var f = new SpikeFilter();
        Assert.Equal(new[] { S(0, 20, false, true) }, f.Admit("s", S(0, 20, false, true), 20));
        Assert.Empty(f.Admit("s", S(5, 26, false, true), 20));
        var commit = f.Admit("s", S(70, 25, false, true), 20);
        Assert.Equal(new[] { S(5, 26, false, true), S(70, 25, false, true) }, commit);
    }

    [Fact]
    public void LevelAnchorStartsAtCurrentLevel()
    {
        // Anchor = when the device reached its current level: the trailing
        // run of same-level connected samples. A level change restarts it.
        var hist = new List<Sample>
        {
            S(0, 51, false, true),
            S(600, 50, false, true),  // reached 50% here — the anchor
            S(1200, 50, false, true), // heartbeat, same level
        };
        Assert.Equal(600, HistoryService.LevelAnchorSecs(hist));
        // A disconnected stretch ends the run: reconnect restarts the timer.
        var withGap = new List<Sample>
        {
            S(0, 50, false, true),
            S(600, 50, false, false),
            S(1200, 50, false, true),
        };
        Assert.Equal(1200, HistoryService.LevelAnchorSecs(withGap));
        Assert.Equal(0, HistoryService.LevelAnchorSecs(new List<Sample>()));
    }

    [Fact]
    public void PseudoAdjustCountsDownFromAnchor()
    {
        // Predicted ~2h left when the level updated (the anchor); 30 real
        // minutes later the displayed remaining is ~1h30m — predicted minus
        // elapsed, floored at zero. Unchanged with no elapsed time.
        var e = new Estimate(2 * 3600, Charging: false);
        Assert.Equal(new Estimate(2 * 3600 - 1800, false), HistoryService.PseudoAdjust(e, 0, 1800));
        Assert.Equal(new Estimate(0, false), HistoryService.PseudoAdjust(e, 0, 5 * 3600));
        Assert.Equal(e, HistoryService.PseudoAdjust(e, 100, 100));
    }

    [Fact]
    public void CyclesAreEquivalentFullCycles()
    {
        // The cycle stat is industry equivalent-full-cycle accounting, not a
        // session count: total qualifying discharge ÷ 100%. Two 50% outings
        // between docks — together one complete charge-empty-recharge
        // sequence — count as 1 cycle, where the old per-span count said 2.
        var samples = new List<Sample>();
        long ts = 0;
        for (int lv = 100; lv >= 50; lv -= 5) // discharge 100→50
        {
            samples.Add(S(ts, lv, false, true));
            ts += 900;
        }
        ts += 100; // flag-flip boundary gap
        for (int lv = 50; lv <= 100; lv += 5) // charge 50→100
        {
            samples.Add(S(ts, lv, true, true));
            ts += 900;
        }
        ts += 100;
        for (int lv = 100; lv >= 50; lv -= 5) // discharge 100→50 again
        {
            samples.Add(S(ts, lv, false, true));
            ts += 900;
        }
        var stats = HistoryService.CycleStatsOf(samples);
        Assert.Equal(1.0, stats.Cycles, 2);
    }

    [Fact]
    public void CyclesSkipSubThresholdSpansAndAccrueFractionally()
    {
        // A 4-point flicker is below the Qualifies floor and mints nothing;
        // qualifying partial sessions accrue fractionally: 10% + 5% of
        // discharge = 0.15 cycles.
        var samples = new List<Sample>
        {
            S(0, 100, false, true),
            S(900, 96, false, true),   // 4 points — below the floor
            S(1000, 96, true, true),
            S(1900, 98, true, true),
            S(2800, 100, true, true),
            S(2900, 100, false, true),
            S(3800, 95, false, true),
            S(4700, 90, false, true),  // 10 points
            S(4800, 90, true, true),
            S(5700, 95, true, true),
            S(6600, 100, true, true),
            S(6700, 100, false, true),
            S(7600, 95, false, true),  // 5 points
        };
        var stats = HistoryService.CycleStatsOf(samples);
        Assert.Equal(0.15, stats.Cycles, 2);
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
    public void WeightingEwmaHalfLifeCutoffAndJunkSpans()
    {
        static Span Span(long end, long active, int moved)
            => new(end - active, end, active, 100, 100 - moved, moved);
        const long Day = 86400;

        // A cycle exactly one half-life (30d) older counts at half weight:
        // (1.0 + 0.5)h / (10% + 5%) = 0.1 h/%.
        var spans = new List<Span>
        {
            Span(10 * Day, 3600, 10),
            Span(10 * Day - 30 * Day, 3600, 10),
        };
        Assert.Equal(1.5 / 15.0, HistoryService.WeightedHoursPerPct(spans)!.Value, 9);

        // Beyond MaxAgeDays (180d) a span drops out entirely.
        var cutoff = new List<Span>
        {
            Span(10 * Day, 3600, 10),
            Span(10 * Day - 181 * Day, 3600, 10),
        };
        Assert.Equal(0.1, HistoryService.WeightedHoursPerPct(cutoff)!.Value, 9);

        // Junk spans are transparent: they neither contribute nor consume —
        // the weight depends only on each qualifying span's own timestamp.
        var junkBetween = new List<Span>
        {
            Span(10 * Day, 3600, 10),
            Span(10 * Day - 15 * Day, 30, 1), // too short + too little move
            Span(10 * Day - 30 * Day, 3600, 10),
        };
        Assert.Equal(1.5 / 15.0, HistoryService.WeightedHoursPerPct(junkBetween)!.Value, 9);

        // Degenerate-only input stays null.
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

    // ------------------------------------------------------------------
    // Current-session blending
    // ------------------------------------------------------------------

    /// <summary>A dock charge session 20→50 at secsPerPct seconds per percent
    /// (rate = secsPerPct/3600 h/%), closed by an unplug sample.</summary>
    private static List<Sample> ChargeSession(long startTs, int from, int to, long secsPerPct)
    {
        var list = new List<Sample> { new(startTs, from, true, true) };
        for (var l = from + 1; l <= to; l++)
        {
            list.Add(new Sample(startTs + (l - from) * secsPerPct, l, true, true));
        }
        list.Add(new Sample(startTs + (to - from) * secsPerPct + 1, to, false, true));
        return list;
    }

    private static List<Sample> Merge(params IReadOnlyList<Sample>[] parts)
        => parts.SelectMany(p => p).ToList();

    [Fact]
    public void CurrentSessionRateThresholds()
    {
        // Below 2% moved → not trusted.
        List<Sample> tiny = [S(0, 80, false, true), S(600, 79, false, true)];
        Assert.Null(HistoryService.CurrentSessionProgress(tiny, charging: false));
        // Below 5 minutes active → not trusted.
        List<Sample> brief = [S(0, 80, false, true), S(120, 78, false, true)];
        Assert.Null(HistoryService.CurrentSessionProgress(brief, charging: false));
        // A 2% drop over 10 min qualifies.
        List<Sample> ok = [S(0, 80, false, true), S(300, 79, false, true), S(600, 78, false, true)];
        var p = HistoryService.CurrentSessionProgress(ok, charging: false)!.Value;
        Assert.Equal(600.0 / 3600.0, p.Hours, 9);
        Assert.Equal(2.0, p.Pct, 9);
        // A disconnected tail is no session at all.
        List<Sample> off = [S(0, 80, false, true), S(600, 78, false, false)];
        Assert.Null(HistoryService.CurrentSessionProgress(off, charging: false));
    }

    [Fact]
    public void BlendShiftsPredictionTowardCurrentSession()
    {
        // Closed cycle: 10% over 3600s → 0.1 h/%. The open current session
        // drains much faster (5% over 1500s → 0.0833 h/%); with pct=5 the
        // blend weight is f = 5/(5+8), so the prediction must sit clearly
        // below the pure-cycle estimate (~15.3ks).
        List<Sample> samples =
        [
            S(0, 100, false, true),
            S(1800, 95, false, true),
            S(3600, 90, false, true),
            S(3601, 90, true, true),
            S(5401, 95, true, true),
            S(5402, 95, false, true),
            S(5702, 94, false, true),
            S(6002, 93, false, true),
            S(6302, 92, false, true),
            S(6602, 91, false, true),
            S(6902, 90, false, true),
        ];
        var e = HistoryService.Predict(samples, 45, chargingNow: false)!.Value;
        Assert.False(e.Charging);
        Assert.InRange(e.Secs, 14400, 14900);
    }

    // ------------------------------------------------------------------
    // Completed-session profiles (nonlinear endpoint extrapolation)
    // ------------------------------------------------------------------

    [Fact]
    public void ChargeProfileCapturesSlowTail()
    {
        // 10→90 at 1 min/% (CC phase), 90→100 at 6 min/% (CV taper). From
        // 95% the profile must predict ≈30 min — the flat rate (~1.6 min/%)
        // would claim ≈8 min.
        var samples = new List<Sample> { S(0, 10, true, true) };
        for (var l = 11; l <= 90; l++)
        {
            samples.Add(S((l - 10) * 60L, l, true, true));
        }
        for (var l = 91; l <= 100; l++)
        {
            samples.Add(S(80 * 60L + (l - 90) * 360L, l, true, true));
        }
        samples.Add(S(80 * 60L + 10 * 360L + 1, 100, false, true)); // unplug: complete

        var prof = HistoryService.ChargeTimeToFullProfile(samples);
        Assert.Equal(3600.0, prof[90]!.Value, 1); // ten CV steps × 6 min
        Assert.Equal(1800.0, prof[95]!.Value, 1); // five CV steps × 6 min
        var e = HistoryService.Predict(samples, 95, chargingNow: true)!.Value;
        Assert.True(e.Charging);
        Assert.InRange(e.Secs, 1740, 1860);
    }

    [Fact]
    public void DischargeProfileCapturesFastTail()
    {
        // 100→20 at 15 min/%, then the last 20% collapses at 6 min/%. From
        // 10% the profile must predict ≈1h, not the flat-rate ≈2.2h.
        var samples = new List<Sample> { S(0, 100, false, true) };
        for (var l = 99; l >= 20; l--)
        {
            samples.Add(S((100 - l) * 900L, l, false, true));
        }
        long tailStart = 80 * 900L;
        for (var l = 19; l >= 0; l--)
        {
            samples.Add(S(tailStart + (20 - l) * 360L, l, false, true));
        }

        var prof = HistoryService.DischargeTimeToEmptyProfile(samples);
        Assert.Equal(3600.0, prof[10]!.Value, 1); // ten tail steps × 6 min
        var e = HistoryService.Predict(samples, 10, chargingNow: false)!.Value;
        Assert.InRange(e.Secs, 3540, 3660);
    }

    [Fact]
    public void PartialSessionsFeedTransitProfile()
    {
        // Top-up / dock habits: a partial charge (unplugged at 96%) and
        // swap-ended discharge cycles never complete, yet every level they
        // actually crossed contributes its observed seconds-per-percent.
        List<Sample> chgSamples =
        [
            S(0, 50, true, true),
            S(900, 70, true, true),
            S(1800, 96, true, true),
            S(1801, 96, false, true),
        ];
        var (_, chgSpans) = HistoryService.ComputeSpans(chgSamples);
        var ct = HistoryService.ChargeTransitProfile(chgSamples, chgSpans);
        Assert.Equal(45.0, ct[50]!.Value, 1);          // 900s / 20 levels
        Assert.Equal(900.0 / 26.0, ct[95]!.Value, 1);  // 900s / 26 levels
        Assert.Null(ct[10]);                           // never visited
        // Without rate fill the endpoint sum can't cross the unobserved
        // 96→100 stretch, so every below-full entry stays null (100 = full).
        var strict = HistoryService.ChargeTimeToFullProfile(chgSamples);
        for (int l = 0; l < 100; l++)
        {
            Assert.Null(strict[l]);
        }
        Assert.Equal(0.0, strict[100]!.Value);

        List<Sample> disSamples =
        [
            S(0, 20, false, true),
            S(900, 15, false, true),
            S(1800, 80, false, true), // swap jump starts a fresh cycle
            S(2700, 75, false, true),
        ];
        var (disSpans, _) = HistoryService.ComputeSpans(disSamples);
        var dt = HistoryService.DischargeTransitProfile(disSamples, disSpans);
        Assert.Equal(180.0, dt[15]!.Value, 1); // 900s / 5 levels
        Assert.Equal(180.0, dt[79]!.Value, 1); // 900s / 5 levels
        Assert.Null(dt[10]);
    }

    [Fact]
    public void PredictFillsUnobservedLevelsFromRate()
    {
        // A completed 50→100 session (uniform 36s/%) plus the device now
        // parked at 30%: levels 32..49 were never observed, so the endpoint
        // sum fills them with the blended rate (36s/%) — 68 × 36 = 2448s.
        // The bare (fill-less) lookup at 30 stays null.
        List<Sample> samples =
        [
            S(0, 50, true, true),
            S(900, 75, true, true),
            S(1800, 100, true, true),
            S(1801, 100, false, true),
            S(3600, 30, true, true),
        ];
        Assert.Null(HistoryService.LookupProfile(HistoryService.ChargeTimeToFullProfile(samples), 30));
        var e = HistoryService.Predict(samples, 32, chargingNow: true)!.Value;
        Assert.True(e.Charging);
        Assert.InRange(e.Secs, 2400, 2500);
    }

    // ------------------------------------------------------------------
    // Battery health / lifespan
    // ------------------------------------------------------------------

    [Fact]
    public void HealthTracksFadingChargeRate()
    {
        // Six month-spaced dock sessions whose charge rate decays linearly
        // 0.25 → 0.20 h/% (−1.67%/mo relative to the 0.24 baseline mean):
        // SOH ≈ 87, fade ≈ 4.2%/mo, ≈1.7 months left to the 80% threshold.
        long month = 30 * 86400;
        var samples = Merge(
            ChargeSession(0 * month, 20, 50, 900),
            ChargeSession(1 * month, 20, 50, 864),
            ChargeSession(2 * month, 20, 50, 828),
            ChargeSession(3 * month, 20, 50, 792),
            ChargeSession(4 * month, 20, 50, 756),
            ChargeSession(5 * month, 20, 50, 720));
        var h = HistoryService.HealthStatsOf(samples)!.Value;
        Assert.InRange(h.SohPct, 86.0, 88.5);
        Assert.InRange(h.FadePerMonthPct, 3.9, 4.4);
        Assert.NotNull(h.MonthsToEol);
        Assert.InRange(h.MonthsToEol!.Value, 1.5, 1.95);
    }

    [Fact]
    public void HealthStableRateNoTrend()
    {
        // Identical charge rates → SOH 100, no fade trend, no EOL horizon.
        long month = 30 * 86400;
        var samples = Merge(
            ChargeSession(0 * month, 20, 50, 900),
            ChargeSession(month, 20, 50, 900),
            ChargeSession(2 * month, 20, 50, 900),
            ChargeSession(3 * month, 20, 50, 900));
        var h = HistoryService.HealthStatsOf(samples)!.Value;
        Assert.InRange(h.SohPct, 99.5, 100.0);
        Assert.True(h.FadePerMonthPct < 0.1);
        Assert.Null(h.MonthsToEol);
    }

    [Fact]
    public void HealthInsufficientData()
    {
        // Two sessions only → null even though they span 90 days…
        long month = 30 * 86400;
        var two = Merge(
            ChargeSession(0, 20, 50, 900),
            ChargeSession(3 * month, 20, 50, 900));
        Assert.Null(HistoryService.HealthStatsOf(two));
        // …and three sessions inside a 30-day window are still too short.
        var dense = Merge(
            ChargeSession(0, 20, 50, 900),
            ChargeSession(15 * 86400, 20, 50, 900),
            ChargeSession(30 * 86400, 20, 50, 900));
        Assert.Null(HistoryService.HealthStatsOf(dense));
    }

    [Fact]
    public void HealthIgnoresShortChargeSpans()
    {
        // Taper-only top-ups (10% moved) sit below the 20% floor: with only
        // those on record there is no usable health signal.
        long month = 30 * 86400;
        var tops = Merge(
            ChargeSession(0, 90, 100, 900),
            ChargeSession(month, 90, 100, 900),
            ChargeSession(2 * month, 90, 100, 900),
            ChargeSession(3 * month, 90, 100, 900));
        Assert.Null(HistoryService.HealthStatsOf(tops));
    }

    [Fact]
    public void RecordEstimateLifecycle()
    {
        // Record's estimate cache must track the series tail through
        // fill / update / disconnect / reconnect. The widget, tray and
        // hover panel read it every second, so an unchanged pass must keep
        // the cached prediction and a vanished device must retire it.
        var oldAppData = Environment.GetEnvironmentVariable("APPDATA");
        var scratch = Path.Combine(Path.GetTempPath(), "razer-taskbar-hist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("APPDATA", scratch);
        try
        {
            // Seed a usable discharge history against real wall-clock time:
            // 60% → 50% over the trailing 30 connected minutes (the instant
            // rate needs ≥5 active minutes and ≥1 point to trust).
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigService.DbPath)!);
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={ConfigService.DbPath}"))
            {
                conn.Open();
                HistoryService.EnsureTables(conn);
                for (int i = 0; i <= 10; i++)
                {
                    Exec(conn,
                        $"INSERT INTO samples VALUES('h', {now - (10 - i) * 180L}, {60 - i}, 0, 1)");
                }
                Exec(conn, $"INSERT INTO devices(handle, name, first_seen, last_seen) VALUES('h', 'Test Mouse', {now}, {now})");
            }
            HistoryService.Init();
            Assert.True(HistoryService.Ready());

            var store = new DeviceStore();
            static RazerDevice Dev(int level, bool connected) =>
                new("Test Mouse", "h", level, false, false, connected, true, DeviceKind.Mouse);

            store.Mutate(m => m["h"] = Dev(50, true));
            HistoryService.Record(store, true);
            var first = HistoryService.EstimateFor("h");
            Assert.NotNull(first); // seeded history predicts remaining time

            // Unchanged pass: the cache holds (±1s of pseudo-countdown drift).
            var second = HistoryService.EstimateFor("h");
            Assert.NotNull(second);
            Assert.InRange(second.Value.Secs, first.Value.Secs - 2, first.Value.Secs);

            // Level update: re-anchored, slightly less remaining.
            store.Mutate(m => m["h"] = Dev(49, true));
            HistoryService.Record(store, true);
            var updated = HistoryService.EstimateFor("h");
            Assert.NotNull(updated);
            Assert.True(updated.Value.Secs < first.Value.Secs);

            // Disconnect retires the estimate; reconnect brings it back.
            store.Mutate(m => m["h"] = Dev(49, false));
            HistoryService.Record(store, true);
            Assert.Null(HistoryService.EstimateFor("h"));
            store.Mutate(m => m["h"] = Dev(49, true));
            HistoryService.Record(store, true);
            Assert.NotNull(HistoryService.EstimateFor("h"));
        }
        finally
        {
            HistoryService.Close();
            Environment.SetEnvironmentVariable("APPDATA", oldAppData);
            try { Directory.Delete(scratch, true); } catch (IOException) { }
        }
    }

    private static void Exec(Microsoft.Data.Sqlite.SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
