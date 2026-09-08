// Tests for ReboundFilter (read-time relaxation-rebound exclusion) and its
// integration into the prediction inputs. Baseline shape is the live
// 2026-09-08 case: the mouse read 61% in use at 03:32, slept overnight,
// woke at 06:56 reporting 65% (voltage-based gauge re-reading the relaxed
// open-circuit voltage), and fell back to 61% by 07:48 once load resumed.

using RazerTaskbar.Core;
using Xunit;

namespace RazerTaskbar.Tests;

public sealed class ReboundFilterTests
{
    private static Sample S(long ts, int level, bool charging = false, bool connected = true)
        => new(ts, level, charging, connected);

    private static int[] Levels(IReadOnlyList<Sample> samples)
        => samples.Select(s => s.Level).ToArray();

    [Fact]
    public void OvernightReboundIsDeflatedToEnvelope()
    {
        // 61 in use → disconnect row (power-save) → wake at 65 → falls back
        // to 61 within 52 minutes → real drain to 60. The whole bump must
        // clamp to the 61 envelope; only the real drop passes through.
        var raw = new List<Sample>
        {
            S(0, 61),
            S(60, 61, connected: false),
            S(12000, 65), // 06:56 wake, relaxed reading
            S(12300, 64),
            S(12600, 63),
            S(12900, 62),
            S(13200, 61), // 07:48 back at the true level
            S(15000, 60), // real drain resumes
        };
        var eff = ReboundFilter.Deflate(raw);
        Assert.Equal(new[] { 61, 61, 61, 61, 61, 61, 61, 60 }, Levels(eff));
    }

    [Fact]
    public void ReboundDoesNotRestartDisplayAnchor()
    {
        // Without deflation the display anchor restarts mid-fall (the raw
        // tail 61-run starts at the 61 sample of the fall, 07:48); with it
        // the clamped plateau merges into one 61-run starting at the wake
        // sample — the countdown keeps running across the bump instead of
        // jumping back up mid-fall.
        var raw = new List<Sample>
        {
            S(0, 61),
            S(60, 61, connected: false),
            S(12000, 65),
            S(12300, 64),
            S(12600, 63),
            S(12900, 62),
            S(13200, 61),
        };
        var eff = ReboundFilter.Deflate(raw);
        Assert.Equal(13200, HistoryService.LevelAnchorSecs(raw));
        Assert.Equal(12000, HistoryService.LevelAnchorSecs(eff));
    }

    [Fact]
    public void ReboundFakeConsumptionIsRemovedFromSessionRate()
    {
        // The fall-back (65 → 61) plus real drain reads as 5%/50min on the
        // raw series — a fake ~6%/h drain that blends into the estimate and
        // shortens it. On the deflated series only the real 1% remains,
        // below the current-session trust floor (CurSessionMinPct).
        var raw = new List<Sample>
        {
            S(0, 61),
            S(60, 61, connected: false),
            S(12000, 65),
            S(12300, 64),
            S(12600, 63),
            S(12900, 62),
            S(13200, 61),
            S(15000, 60),
        };
        var rawCur = HistoryService.CurrentSessionProgress(raw, false);
        Assert.NotNull(rawCur);
        Assert.Equal(5, rawCur!.Value.Pct);
        Assert.Null(HistoryService.CurrentSessionProgress(ReboundFilter.Deflate(raw), false));
    }

    [Fact]
    public void ReboundNoLongerShortensPredictedRemaining()
    {
        // End to end: nine slow background percent (15min each), overnight
        // sleep, the wake bump falling back fast, then real drain. The raw
        // series blends the bump's fake drain into the rate and the transit
        // fill, predicting far less remaining time than the deflated one.
        var raw = new List<Sample>();
        for (int i = 0; i <= 9; i++)
        {
            raw.Add(S(i * 900, 70 - i));
        }
        raw.Add(S(8101, 61, connected: false));
        raw.AddRange(new[]
        {
            S(9000, 65),
            S(9060, 64),
            S(9120, 63),
            S(9180, 62),
            S(9240, 61),
            S(10440, 60),
            S(11640, 59),
        });
        var rawEst = HistoryService.Predict(raw, 59, false);
        var effEst = HistoryService.Predict(ReboundFilter.Deflate(raw), 59, false);
        Assert.NotNull(rawEst);
        Assert.NotNull(effEst);
        Assert.True(effEst!.Value.Secs > rawEst!.Value.Secs,
            $"deflated {effEst.Value.Secs}s should exceed raw {rawEst.Value.Secs}s");
    }

    [Fact]
    public void SustainedRiseIsAcceptedAsRealRecalibration()
    {
        // A +4 rise that survives two hours of connected time is a genuine
        // gauge recalibration (relaxation decays within the hour under
        // load): the sample crossing ReboundAcceptSecs adopts the new level.
        // Samples every ≤30min stand in for the connected heartbeat.
        var raw = new List<Sample>
        {
            S(0, 61),
            S(60, 65),    // bump opens, clamped
            S(1860, 65),  // 1800s accumulated
            S(3660, 65),  // 3600s
            S(5460, 65),  // 5400s — still provisional
            S(7260, 65),  // 7200s — accepted
            S(7860, 64),  // normal drain from the new envelope
        };
        Assert.Equal(new[] { 61, 61, 61, 61, 61, 65, 64 }, Levels(ReboundFilter.Deflate(raw)));
    }

    [Fact]
    public void SleepDoesNotAdvanceAcceptanceClock()
    {
        // A device that wakes still inflated after a long power-save stays
        // provisional: sleep contributes no connected time, so the reading
        // must fall back (or stay up for two hours) before it is trusted.
        var raw = new List<Sample>
        {
            S(0, 61),
            S(60, 65),                    // bump opens
            S(40000, 65, connected: false), // asleep >11h — no clock advance
            S(40300, 65),                 // awake again, only 300s accumulated
            S(40600, 61),                 // falls back: confirmed artifact
            S(40900, 60),
        };
        Assert.Equal(new[] { 61, 61, 61, 61, 61, 60 }, Levels(ReboundFilter.Deflate(raw)));
    }

    [Fact]
    public void RealSwapRisePassesThrough()
    {
        // Rises >= RiseSuspectPct were confirmed by SpikeFilter at write
        // time (real swap / recalibration): passed through, envelope reset.
        var raw = new List<Sample>
        {
            S(0, 61),
            S(60, 91),
            S(120, 90),
        };
        Assert.Equal(new[] { 61, 91, 90 }, Levels(ReboundFilter.Deflate(raw)));

        // The 5-point boundary belongs to SpikeFilter too.
        var boundary = new List<Sample>
        {
            S(0, 61),
            S(60, 66),
            S(120, 65),
        };
        Assert.Equal(new[] { 61, 66, 65 }, Levels(ReboundFilter.Deflate(boundary)));
    }

    [Fact]
    public void ChargingResetsEnvelope()
    {
        // Real charge re-bases the envelope; a small rise after unplug is
        // then clamped against the new base.
        var raw = new List<Sample>
        {
            S(0, 61),
            S(60, 70, charging: true),
            S(120, 70),
            S(180, 73),
            S(240, 70),
            S(300, 69),
        };
        Assert.Equal(new[] { 61, 70, 70, 70, 70, 69 }, Levels(ReboundFilter.Deflate(raw)));
    }

    [Fact]
    public void PartialFallbackAcceptedOnlyAfterWindow()
    {
        // A bump that only partially falls back (65 → 63) hovers above the
        // envelope: provisional until it either survives the window
        // (accepted at 63) or reaches it.
        var accepted = new List<Sample>
        {
            S(0, 61),
            S(60, 65),
            S(1860, 64),
            S(3660, 64),
            S(5460, 63),
            S(7260, 63), // 7200s connected — accepted
            S(8000, 62),
        };
        Assert.Equal(new[] { 61, 61, 61, 61, 61, 63, 62 }, Levels(ReboundFilter.Deflate(accepted)));

        var resolved = new List<Sample>
        {
            S(0, 61),
            S(60, 65),
            S(600, 63),
            S(1200, 61),
            S(1800, 60),
        };
        Assert.Equal(new[] { 61, 61, 61, 61, 60 }, Levels(ReboundFilter.Deflate(resolved)));
    }

    [Fact]
    public void UnplugMirrorArtifactIsClamped()
    {
        // Mirror shape after unplug: the reading sags under residual load
        // (99 → 97), then bounces back as the gauge re-reads the recovering
        // voltage (97 → 99) before settling. The bounce must clamp to the
        // settled envelope.
        var raw = new List<Sample>
        {
            S(0, 99, charging: true),
            S(60, 97),
            S(120, 98),
            S(180, 99),
            S(240, 97),
            S(300, 96),
        };
        Assert.Equal(new[] { 99, 97, 97, 97, 97, 96 }, Levels(ReboundFilter.Deflate(raw)));
    }

    [Fact]
    public void TrivialSeriesPassThrough()
    {
        Assert.Empty(ReboundFilter.Deflate(new List<Sample>()));
        var single = new List<Sample> { S(0, 55) };
        Assert.Equal(new[] { 55 }, Levels(ReboundFilter.Deflate(single)));

        // A second bump after a resolved one starts its clock from zero:
        // both stay clamped, neither inherits the other's accumulated time.
        var twoBumps = new List<Sample>
        {
            S(0, 61),
            S(60, 65),      // bump 1
            S(960, 65),     // 900s accumulated
            S(1860, 64),    // 1800s
            S(2400, 61),    // resolved — bump 1's clock dies
            S(3000, 60),    // real drain
            S(3060, 64),    // bump 2: fresh clock (0s)
            S(3960, 64),    // 900s — not 900+2340
            S(4860, 64),    // 1800s
            S(5760, 64),    // 2700s
            S(6660, 63),    // 3600s
            S(7560, 63),    // 4500s
            S(8460, 63),    // 5400s
            S(9360, 62),    // 6300s — still provisional
            S(10260, 62),   // 7200s — accepted
        };
        // Bump 2 clamps against the envelope as it stands after the real
        // drain (60), not against bump 1's level.
        Assert.Equal(new[] { 61, 61, 61, 61, 61, 60, 60, 60, 60, 60, 60, 60, 60, 60, 62 },
            Levels(ReboundFilter.Deflate(twoBumps)));
    }
}
