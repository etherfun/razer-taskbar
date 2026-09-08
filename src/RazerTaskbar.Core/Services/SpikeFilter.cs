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
//
// Two glitch shapes are recognized while a hold is open:
//  - fall-back to within TolerancePct of the pre-jump level (the pre-jump
//    reading was fresh); and
//  - the held segment OSCILLATING across ≥ JumpPct (e.g. 100 → 65 → 100):
//    a real swap moves once and stays, so a segment that swings back can
//    only be a glitch. This catches the reconnect case where the pre-jump
//    anchor is a stale disconnected heartbeat (device at 65 reconnecting
//    through a flapping dongle first reports 100; the true 65 is outside
//    tolerance of the 8-minute-old 61) — seen live 2026-09-08 06:56, where
//    expiry then committed the glitch and poisoned the anchor for the
//    oscillations that followed. On an oscillation drop NOTHING is trusted:
//    the next reading is the device's true level and admits normally.

namespace RazerTaskbar.Core;

public sealed class SpikeFilter
{
    /// <summary>An instantaneous level change of at least this many points
    /// is suspect — real charging/discharging moves about one point per
    /// poll.</summary>
    public const int JumpPct = 20;

    /// <summary>An instantaneous level RISE of at least this many points
    /// while not charging is suspect even below JumpPct: discharging can
    /// never gain, and a genuine plug-in flips the charging flag (gradual
    /// top-ups move a point per poll). Covers near-full glitches like
    /// 85% → 100% that the JumpPct threshold would admit outright.</summary>
    public const int RiseSuspectPct = 5;

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
        public int Peak;
        public int Trough;
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
            int delta = s.Level - lastWrittenLevel;
            bool suspect = Math.Abs(delta) >= JumpPct
                || (!s.Charging && delta >= RiseSuspectPct);
            if (!suspect)
            {
                return new[] { s };
            }
            hold = new Hold { PreLevel = lastWrittenLevel, StartTs = s.Ts, Peak = s.Level, Trough = s.Level };
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
        // The held segment swung across the jump and back: an oscillation,
        // never a real swap (a swap moves once and stays). Trust nothing in
        // it — the next reading is the device's true level and admits
        // normally against the untouched pre-jump series.
        int peak = Math.Max(hold.Peak, s.Level);
        int trough = Math.Min(hold.Trough, s.Level);
        if (peak - trough >= JumpPct)
        {
            _holds.Remove(handle);
            return Array.Empty<Sample>();
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
            hold.Peak = peak;
            hold.Trough = trough;
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
