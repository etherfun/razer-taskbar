// Read-time relaxation-rebound exclusion for battery history. A wireless
// device that idles long enough for its cell to relax reports a HIGHER level
// when it wakes: 61% in use (03:32) → 65% after a night asleep (06:56) →
// 61% again within the hour (07:48), seen live 2026-09-08. The wake reading
// is the device's voltage-based gauge re-estimating from the relaxed
// open-circuit voltage, not new charge — under load the iR drop re-appears
// instantly and the diffusion overpotential within minutes, so the reading
// falls back (BU-903: voltage-based SoC is only meaningful after hours of
// rest). Left in the series it poisons the prediction three ways: the
// inflated level becomes levelNow, the fall-back counts as real consumption
// (a fake multi-percent-per-hour drain that blends into the rate and
// shortens the estimate), and the display anchor restarts mid-bump.
//
// This filter rewrites the series for PREDICTION purposes only — charts and
// the displayed percentage keep the raw readings, the estimate math sees an
// envelope. It is the percentage-only stand-in for what the literature does
// with coulomb counting (discharge monotonicity + recalibrate-at-rest):
//
//  - while NOT charging the level must be non-increasing. Any rise of
//    1..4 points over the running envelope is clamped to it as a provisional
//    relaxation bump (rises >= RiseSuspectPct already went through
//    SpikeFilter at write time and are real swaps/recalibrations — passed
//    through, and they reset the envelope);
//  - a clamped bump that falls back to the envelope is confirmed
//    relaxation and stays clamped forever;
//  - a clamped bump that SURVIVES ReboundAcceptSecs of connected time is
//    not relaxation (relaxation decays within the hour once load resumes)
//    but a genuine gauge recalibration — accepted as the new envelope.
//    Sleep does not advance the acceptance clock: only connected intervals
//    shorter than GapBreakSecs count, so a device that wakes still inflated
//    after a long power-save stays provisional until it stays up.
//  - charging samples pass through and reset the envelope (real charge),
//    which also covers the mirror artifact after unplug (sag then bounce).
//
// Call this ONCE per series, on the raw DB rows, at the read entry points
// (Record, CycleStatsOf, HealthStatsOf, the history page cycle list). It is
// not idempotent for accepted recalibrations: an accepted sub-threshold rise
// re-enters the 1..4 clamp range on a second pass.

namespace RazerTaskbar.Core;

public static class ReboundFilter
{
    /// <summary>A clamped rise that survives this much CONNECTED time is a
    /// genuine gauge recalibration, not relaxation: under load relaxation
    /// decays within the hour (iR drop instant, diffusion overpotential
    /// minutes), so a reading that holds for hours of use is real. Two hours
    /// keeps the common overnight rebound (wake inflated, fall back within
    /// the hour) safely on the artifact side.</summary>
    public const long ReboundAcceptSecs = 2 * 3600;

    /// <summary>Rewrite `samples` (the raw series, oldest first) so the
    /// estimate math never sees a discharge-side relaxation bump: clamped
    /// samples keep their timestamp/flags but hold the envelope level.
    /// Charging samples and real swaps pass through untouched.</summary>
    public static List<Sample> Deflate(IReadOnlyList<Sample> samples)
    {
        var outList = new List<Sample>(samples.Count);
        if (samples.Count == 0)
        {
            return outList;
        }
        int env = samples[0].Level;
        bool elevated = false; // a provisional (clamped) bump is open
        long elevatedSecs = 0; // connected time accumulated inside the bump
        long prevTs = samples[0].Ts;
        foreach (var s in samples)
        {
            int rise = s.Level - env;
            bool clamp = !s.Charging && rise > 0 && rise < SpikeFilter.RiseSuspectPct;
            if (clamp && elevated)
            {
                if (s.Connected && s.Ts - prevTs <= HistoryService.GapBreakSecs)
                {
                    elevatedSecs += s.Ts - prevTs;
                }
                // Survived ReboundAcceptSecs of connected time: the gauge
                // recalibrated for real, adopt the new level.
                if (s.Connected && elevatedSecs >= ReboundAcceptSecs)
                {
                    clamp = false;
                }
            }
            if (clamp)
            {
                elevated = true;
                outList.Add(s with { Level = env });
            }
            else
            {
                // Charging sample, real drain, a fall-back (which confirms
                // the open bump as relaxation), an accepted recalibration,
                // or a write-time-confirmed swap (rise >= RiseSuspectPct):
                // pass through, re-anchor the envelope here, and close the
                // bump (its acceptance clock dies with it).
                env = s.Level;
                elevated = false;
                elevatedSecs = 0;
                outList.Add(s);
            }
            prevTs = s.Ts;
        }
        return outList;
    }
}
