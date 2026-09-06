// Write-time anomaly exclusion for battery history sampling: an
// instantaneous level jump (e.g. 50% → 100%) is HELD out of battery.db for
// a short confirmation window. If the level falls back near the pre-jump
// value within the window, the jump was a reporting glitch and the whole
// held segment is dropped — it never reaches the database, so charts,
// cycles and predictions stay clean. If the window expires with the level
// still away from the pre-jump value, the jump was real (battery swap /
// reconnect) and the held samples are committed in order. This runs before
// the read-time glitch handling in HistoryService.ComputeSpans, which
// therefore sees cleaner data.

namespace RazerTaskbar.Core;

public sealed class SpikeFilter
{
    /// <summary>An instantaneous level change of at least this many points
    /// is suspect — real charging/discharging moves about one point per
    /// poll.</summary>
    public const int JumpPct = 20;

    /// <summary>Falling back to within this many points of the pre-jump
    /// level confirms a reporting glitch (the level kept drifting a little
    /// while the bogus value was shown).</summary>
    public const int TolerancePct = 3;

    /// <summary>Confirmation window: a jump still unresolved this long after
    /// it started commits as real.</summary>
    public const long GraceSecs = 60;

    private sealed class Hold
    {
        public int PreLevel;
        public long StartTs;
        public readonly List<Sample> Pending = new();
    }

    private readonly Dictionary<string, Hold> _holds = new();

    /// <summary>Decide what to do with sample `s` for `handle`;
    /// `lastWrittenLevel` is the newest level already in the series (the
    /// device's own level when the series is empty). Returns the samples to
    /// write now: empty while holding or after dropping a glitch segment,
    /// the held backlog plus `s` when a jump commits.</summary>
    public IReadOnlyList<Sample> Admit(string handle, Sample s, int lastWrittenLevel)
    {
        if (!_holds.TryGetValue(handle, out var hold))
        {
            if (Math.Abs(s.Level - lastWrittenLevel) < JumpPct)
            {
                return new[] { s };
            }
            hold = new Hold { PreLevel = lastWrittenLevel, StartTs = s.Ts };
            hold.Pending.Add(s);
            _holds[handle] = hold;
            return Array.Empty<Sample>();
        }
        // Back near the pre-jump level inside the window: reporting glitch —
        // drop the whole held segment; the returning sample itself is real.
        if (Math.Abs(s.Level - hold.PreLevel) <= TolerancePct)
        {
            _holds.Remove(handle);
            return new[] { s };
        }
        // Still unresolved when the window expires: real swap/reconnect —
        // commit the backlog in order, then resume normal admission.
        if (s.Ts - hold.StartTs >= GraceSecs)
        {
            _holds.Remove(handle);
            var commit = new List<Sample>(hold.Pending) { s };
            return commit;
        }
        // Inside the window: keep holding, deduping unchanged readings so a
        // hold does not accumulate one row per parse pass.
        var last = hold.Pending[^1];
        if (last.Level != s.Level || last.Charging != s.Charging || last.Connected != s.Connected)
        {
            hold.Pending.Add(s);
        }
        return Array.Empty<Sample>();
    }

    /// <summary>Drop an unconfirmed hold for `handle` (device vanished or
    /// history turned off mid-jump): treated as a glitch, nothing held is
    /// ever written; the caller records the disconnect from the last
    /// written sample, i.e. the pre-jump level.</summary>
    public void Reset(string handle) => _holds.Remove(handle);
}
