// Port of the data types in src/history.rs.

namespace RazerTaskbar.Core;

public readonly record struct Sample(long Ts, int Level, bool Charging, bool Connected);

/// <summary>One discharge cycle or charge session.</summary>
public readonly record struct Span(
    long StartTs,
    long EndTs,
    /// <summary>Wall time the device was actually powered on (off/unknown excluded).</summary>
    long ActiveSecs,
    int LevelStart,
    int LevelEnd,
    /// <summary>Percent points moved during counted (active) intervals only.</summary>
    int MovedPct)
{
    public bool Qualifies(int minDropPct, long minActiveSecs)
        => MovedPct >= minDropPct && ActiveSecs >= minActiveSecs;
}

/// <summary>Predicted remaining time (charging=false) or time to full (true).</summary>
public readonly record struct Estimate(long Secs, bool Charging);

/// <summary>Aggregate stats for a sample series (history page header).</summary>
public readonly record struct CycleStats(
    int Cycles,
    double? UseHoursPerPct,
    double? ChargeHoursPerPct);

/// <summary>Battery health / lifespan estimate (history page card). SohPct
/// is relative to the earliest recorded charge sessions; FadePerMonthPct is
/// the fitted capacity-fade trend; MonthsToEol extrapolates to the 80%
/// end-of-life threshold — null while the trend reads as stable, 0 once the
/// threshold is already met.</summary>
public readonly record struct HealthStats(
    double SohPct,
    double FadePerMonthPct,
    double? MonthsToEol);
