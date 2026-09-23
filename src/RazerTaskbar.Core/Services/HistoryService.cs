// Port of src/history.rs: SQLite sampling, usage-cycle computation, usage-time
// prediction (battery.db lives next to settings.json, retained forever; schema
// and semantics are byte-compatible with the Rust build so the two can share
// a database — just never run both at once).
//
// - Sampling runs on the watcher thread after every parse pass. A point is
//   written when (connected, charging, level) changes, plus a heartbeat.
// - A discharge cycle spans across power-save shutdowns: a disconnected
//   stretch contributes NO active time but does NOT end the cycle.
// - A battery swap (large level JUMP while discharging) ends the cycle; a
//   jump that immediately falls back is a reporting glitch and is dropped.
// - SpikeFilter (write time): an instantaneous jump (e.g. 65% → 100% for
//   20s, seen live 2026-09-06) is held out of battery.db for 60s; falling
//   back near the pre-jump level within the window drops the whole segment
//   before it is ever recorded, expiry commits it as a real swap. A segment
//   that swings back across the jump (oscillation) is also dropped — that
//   is the reconnect shape where the pre-jump anchor is a stale
//   disconnected heartbeat (seen live 2026-09-08 06:56: 61% con=0 for 8
//   minutes, reconnect first reports 100%, true level 65% — outside the ±3
//   tolerance of the stale 61 — so expiry committed the glitch and its
//   poisoned 100 anchor admitted the follow-up oscillation).
// - Scrub (startup): CommittedGlitchTs applies the same retroactive rule to
//   the loaded series — a suspect jump whose level fell back within 60s is
//   deleted from battery.db and memory — healing rows recorded before the
//   write-time guard existed or through its bugs.
// - ReboundFilter (read time): a device that idles long enough for its cell
//   to relax wakes reporting a HIGHER level (61% → 65% → back to 61% within
//   the hour, seen live 2026-09-08) — the voltage-based gauge re-read the
//   relaxed open-circuit voltage, and the fall-back under load is not real
//   consumption. Prediction, cycle stats and the display anchor run on the
//   deflated (envelope) series; charts and the shown percentage stay raw.
//   A bump that survives two hours of connected time is a real gauge
//   recalibration and is adopted.
// - Prediction is a three-tier estimator:
//     1. per-level transit profile: every session contributes its observed
//        seconds-per-percent between successive high/low-water levels
//        (EWMA, 30-day half-life); the endpoint estimate sums these per
//        level, filling never-observed levels with the tier-2 rate. Top-up
//        / dock habits (sessions that never reach 100% / 0%) still count —
//        they simply cover fewer levels — and the CC-CV charge taper and
//        fast drain tail come from real shape data wherever it exists;
//     2. blended rate: pooled hours-per-percent over past cycles with
//        EWMA recency weights (halves every 30 days, data beyond 180 days
//        dropped — small-cell aging literature: calendar fade and habit
//        shifts accumulate in wall-clock time, so weights follow age in
//        days, not cycle counts) blended with the current session's own
//        observed rate (its weight grows with the percent already moved);
//     3. instant rate over the trailing 30 minutes (fresh installs).
// - Battery health (history page): charge speed is the clean capacity-fade
//   proxy derivable from % samples — charge current is set by the dock, so
//   unlike drain rate it is usage-independent, and capacity fade shortens
//   hours-per-percent proportionally. SOH = recent EWMA charge rate
//   relative to the earliest recorded sessions; a least-squares fade trend
//   is extrapolated to the industry 80% end-of-life threshold.
// - Pseudo countdown (display): the shown remaining is anchored when the
//   level updates and shrinks in real time (predicted minus elapsed).

using Microsoft.Data.Sqlite;

namespace RazerTaskbar.Core;

public static class HistoryService
{
    /// <summary>Heartbeat while a device sits unchanged. Must stay well below
    /// GapBreakSecs, which treats longer silence as off/unknown.</summary>
    private const long HeartbeatSecs = 15 * 60;

    /// <summary>Silence longer than this is not counted as active time but does
    /// not end the running cycle. The chart uses the same threshold for gap
    /// bands.</summary>
    public const long GapBreakSecs = 30 * 60;

    /// <summary>A level rise of at least this many points while discharging is
    /// a battery swap, not data noise.</summary>
    private const int SwapJumpPct = 30;

    /// <summary>EWMA half-life for cycle weights: a cycle's weight halves
    /// every 30 days of age. Scale rationale (small Li-ion/LiPo cells in
    /// peripherals): calendar aging is 1–3%/yr mild / 5–20%/yr consumer
    /// conditions and accumulates in wall-clock time, so month-old data is
    /// only fractionally stale while usage habits shift on week scales.</summary>
    private const double HalfLifeDays = 30.0;

    /// <summary>Cycles older than this are dropped: at ~180 days accumulated
    /// calendar+cycle fade (~2–5%) breaks the "same battery" assumption, and
    /// post-knee degradation is tracked by the recent-weighted tail anyway.</summary>
    private const double MaxAgeDays = 180.0;

    /// <summary>Blending constant for the current session: a session that has
    /// moved BlendKPct percent points carries half the total weight, so a
    /// fresh habit shows up quickly while early-session noise stays damped.</summary>
    private const double BlendKPct = 8.0;

    /// <summary>The open current session must have moved at least this many
    /// percent (and CurSessionMinActiveSecs) before its rate is trusted.</summary>
    private const double CurSessionMinPct = 2.0;

    private const long CurSessionMinActiveSecs = 5 * 60;

    /// <summary>Profile lookup: if the exact level has no entry, the nearest
    /// entry within this many points is used (closer wins; tie picks the
    /// longer/conservative side).</summary>
    private const int ProfileLevelTolerance = 2;

    // Battery health (history page).
    /// <summary>Charge sessions below this much moved percent are excluded:
    /// a 90→100 taper-only session quantizes too coarsely for a SOH signal.</summary>
    private const int HealthMinChargePct = 20;

    private const int HealthMinSpans = 3;

    /// <summary>The recorded window must span at least this many days before
    /// a fade trend means anything.</summary>
    private const double HealthMinWindowDays = 60.0;

    /// <summary>Industry end-of-life convention (BU-808): rated cycle count
    /// ends at 80% of design capacity.</summary>
    private const double HealthEolPct = 80.0;

    /// <summary>Fade slower than this per month reads as "stable" (below the
    /// quantization noise of percentage readings).</summary>
    private const double HealthStableFadePerMonth = 0.1;

    /// <summary>Sanity cap on the fitted fade trend (%/month).</summary>
    private const double HealthMaxFadePerMonth = 5.0;

    /// <summary>A cycle/session must move at least this many percent to count.</summary>
    private const int MinSpanDropPct = 5;

    /// <summary>…and must span at least this much active time.</summary>
    private const long MinSpanActiveSecs = 60;

    /// <summary>Fallback (no usable cycle yet): instant rate over the last window.</summary>
    private const long InstantWindowSecs = 30 * 60;
    private const long InstantMinActiveSecs = 5 * 60;
    private const double InstantMinDropPct = 1.0;

    // One gate mirrors history.rs locking all five statics in record();
    // the DB connection has its own lock (short blocking queries only).
    private static readonly object Sync = new();
    private static readonly object DbLock = new();
    private static SqliteConnection? _db;

    private static Dictionary<string, List<Sample>> _series = new();
    private static Dictionary<string, string> _names = new();
    /// <summary>handle → the link its last reading arrived on, as persisted in
    /// the devices table (<see cref="BatteryTransport"/>).</summary>
    private static Dictionary<string, BatteryTransport> _sources = new();
    /// <summary>handle → present in the last record pass (drives disconnect detection).</summary>
    private static Dictionary<string, bool> _seen = new();
    /// <summary>handle → (prediction, display anchor), both computed on the
    /// rebound-deflated series at record time (see <see cref="ReboundFilter.Deflate"/>).</summary>
    private static Dictionary<string, (Estimate Est, long AnchorSecs)> _estimates = new();

    /// <summary>Open battery.db, create tables, mirror existing rows into memory.</summary>
    public static void Init()
    {
        SqliteConnection conn;
        try
        {
            conn = OpenDb();
        }
        catch (Exception e)
        {
            Log.Error($"razer-taskbar: battery.db open failed: {e.Message}");
            return;
        }
        try
        {
            LoadSeries(conn);
        }
        catch (Exception e)
        {
            Log.Error($"razer-taskbar: battery.db load failed: {e.Message}");
            return;
        }
        lock (DbLock)
        {
            _db = conn;
        }
        MergeAliasedRows();
        ScrubGlitches();
    }

    /// <summary>Startup heal for identity pollution: one physical device can
    /// take root under a synthesized fallback handle (HID:{pid} / BLE:{mac})
    /// when the vendor serial query went unanswered at startup, and both it
    /// and the later real-serial row end up in battery.db — two history
    /// series for one device. Every fallback-keyed row whose (unique)
    /// same-name serial row exists is folded into it: samples re-pointed,
    /// alias row dropped, in-memory series merged.</summary>
    private static void MergeAliasedRows()
    {
        List<(string Src, string Dst)> aliases;
        lock (Sync)
        {
            aliases = AliasPairs(_names);
        }
        foreach (var (src, dst) in aliases)
        {
            Log.Info($"history: merged aliased HID device rows: {src} -> {dst}");
            MergeAlias(src, dst);
        }
    }

    /// <summary>Pure pairing rule for <see cref="MergeAliasedRows"/>: for each
    /// fallback handle (contains ':') the same-name serial handle — but only
    /// when it is unambiguous (two same-name serial rows could be two units
    /// of one model; the alias then stays).</summary>
    internal static List<(string Src, string Dst)> AliasPairs(IReadOnlyDictionary<string, string> names)
    {
        var aliases = new List<(string, string)>();
        foreach (var (src, srcName) in names)
        {
            if (src.IndexOf(':') < 0)
            {
                continue;
            }
            string? dst = null;
            foreach (var (key, name) in names)
            {
                if (key.Contains(':')
                    || !string.Equals(name, srcName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (dst is not null)
                {
                    dst = null; // two same-name serial rows: could be two units — leave alone
                    break;
                }
                dst = key;
            }
            if (dst is not null)
            {
                aliases.Add((src, dst));
            }
        }
        return aliases;
    }

    /// <summary>Fold a fallback identity's history into its resolved serial
    /// identity (one physical device, two handles over time). Merges the
    /// in-memory series and — while the DB is open — moves the samples and
    /// drops the alias row. On a timestamp tie the dst sample (recorded under
    /// the canonical identity) wins.</summary>
    internal static void MergeAlias(string src, string dst)
    {
        lock (Sync)
        {
            if (_series.TryGetValue(src, out var srcHist))
            {
                var dstHist = _series.TryGetValue(dst, out var h) ? h : new List<Sample>();
                dstHist.AddRange(srcHist);
                var merged = dstHist.OrderBy(s => s.Ts).ToList(); // stable: dst wins ties
                _series.Remove(src);
                _series[dst] = merged;
            }
            _names.Remove(src);
            _estimates.Remove(src);
        }
        SqliteConnection? conn;
        lock (DbLock)
        {
            conn = _db;
        }
        if (conn is null)
        {
            return;
        }
        lock (DbLock)
        {
            // One connection serves every thread; commands must serialize on it.
            MergeAlias(conn, src, dst);
        }
        lock (Sync)
        {
            // The merged series changed shape: its cached prediction is stale.
            RecomputeEstimateLocked(dst);
        }
    }

    /// <summary>DB half of <see cref="MergeAlias"/> on an explicit connection
    /// (testable against a scratch database).</summary>
    internal static void MergeAlias(SqliteConnection conn, string src, string dst)
    {
        try
        {
            Exec(conn,
                "INSERT OR IGNORE INTO samples(handle, ts, level, charging, connected) " +
                "SELECT $dst, ts, level, charging, connected FROM samples WHERE handle = $src",
                ("$src", src), ("$dst", dst));
            Exec(conn, "DELETE FROM samples WHERE handle = $src", ("$src", src));
            Exec(conn, "DELETE FROM devices WHERE handle = $src", ("$src", src));
        }
        catch (Exception e)
        {
            Log.Error($"history: alias merge failed ({src} -> {dst})", e);
        }
    }

    /// <summary>Startup heal for committed glitch segments: rows recorded
    /// before the write-time SpikeFilter existed (or through its stale-
    /// anchor blind spot at reconnects) still sit in battery.db as 1-2
    /// bogus spikes. Retroactively applies the same admission rule to the
    /// loaded series — a suspect jump whose level fell back within the
    /// grace window was never real (a swap moves once and stays) — and
    /// deletes those rows from memory and the database.</summary>
    private static void ScrubGlitches()
    {
        var doomed = new Dictionary<string, List<long>>();
        lock (Sync)
        {
            foreach (var (h, hist) in _series)
            {
                var ts = CommittedGlitchTs(hist);
                if (ts.Count > 0)
                {
                    doomed[h] = ts;
                }
            }
        }
        if (doomed.Count == 0)
        {
            return;
        }
        SqliteConnection? conn;
        lock (DbLock)
        {
            conn = _db;
        }
        int removed = 0;
        if (conn is not null)
        {
            lock (DbLock)
            {
                foreach (var (h, ts) in doomed)
                {
                    removed += DeleteSamples(conn, h, ts);
                }
            }
        }
        lock (Sync)
        {
            foreach (var (h, ts) in doomed)
            {
                if (_series.TryGetValue(h, out var hist))
                {
                    hist.RemoveAll(s => ts.Contains(s.Ts));
                }
            }
        }
        Log.Info($"history: scrubbed {removed} committed glitch sample(s) in {doomed.Count} series");
    }

    /// <summary>Pure rule for <see cref="ScrubGlitches"/>: timestamps of the
    /// samples a series is better off without. A jump suspect under the
    /// SpikeFilter admission rule whose level returns to near the anchor
    /// level — or swings back across the jump — inside GraceSecs was a
    /// reporting glitch; the jump rows up to (not including) the returning
    /// sample are deleted, the returning sample stays (it is the device's
    /// true reading). A jump the device took into a disconnect was never
    /// live-confirmed either: the next connected reading after the off
    /// stretch re-anchors, and the jump plus the stale off plateau go. A
    /// jump with no fall-back inside the window is a real swap/reconnect
    /// and is kept. The rule runs to a fixed point: one sweep leaves the last
    /// row of a glitch plateau in place when its hunt re-anchored on a row
    /// that was itself stale (live 2026-09-18: the plateau's final 80% closed
    /// the segment, the true 36% followed 9 s later, and the leftover pair
    /// read as a fresh spike in the chart). Re-judging the survivors against
    /// the reduced series condemns it and matches the write-time hold, which
    /// drops the whole pending run the moment a reading falls back.</summary>
    internal static List<long> CommittedGlitchTs(IReadOnlyList<Sample> hist)
    {
        var doomed = new List<long>();
        var work = hist.ToList();
        while (work.Count > 1)
        {
            var round = GlitchPass(work);
            if (round.Count == 0)
            {
                break;
            }
            var drop = new HashSet<long>(round);
            doomed.AddRange(round);
            work.RemoveAll(s => drop.Contains(s.Ts));
        }
        return doomed;
    }

    /// <summary>One sweep of <see cref="CommittedGlitchTs"/>'s rule over a
    /// series: the timestamps that sweep condemns.</summary>
    private static List<long> GlitchPass(IReadOnlyList<Sample> hist)
    {
        var doomed = new List<long>();
        int i = 0;
        while (i + 1 < hist.Count)
        {
            var a = hist[i];
            var b = hist[i + 1];
            int rise = b.Level - a.Level;
            // Same suspect-jump predicate as SpikeFilter.Admit — keep the
            // two in sync (write-time hold vs startup scrub).
            bool suspect = Math.Abs(rise) >= SpikeFilter.JumpPct
                || (!b.Charging && rise >= SpikeFilter.RiseSuspectPct);
            if (!suspect)
            {
                i++;
                continue;
            }
            int k = -1;
            int peak = b.Level, trough = b.Level;
            // A disconnect after (or on) the jump voids live confirmation:
            // the device took the jump into power-off, and past builds
            // parroted the store's last level into the disconnected
            // heartbeats — a 100% plateau across hours that the grace expiry
            // then committed as a "real swap" (seen live 2026-09-10 05:04:
            // 58% → glitch-100 → 5.5h of 100% conn=0 → wake at the true
            // 62%). Once the hunt crosses a disconnected row, the next
            // CONNECTED reading re-anchors the series and everything before
            // it (jump + stale plateau) is doomed, whatever its level.
            bool bridgedOff = !b.Connected;
            for (int m = i + 2; m < hist.Count; m++)
            {
                var r = hist[m];
                if (!r.Connected)
                {
                    bridgedOff = true;
                    // An honest disconnect echo carries the series' trusted
                    // (pre-jump) level: it confirms the glitch exactly like a
                    // connected fall-back, whatever its age. A stale-plateau
                    // row (the parroted glitch value) is far from the anchor
                    // and keeps hunting for the live return.
                    if (Math.Abs(r.Level - a.Level) <= SpikeFilter.TolerancePct)
                    {
                        k = m;
                        break;
                    }
                    continue;
                }
                if (bridgedOff)
                {
                    k = m;
                    break;
                }
                if (r.Ts - b.Ts > SpikeFilter.GraceSecs)
                {
                    break; // window expired with the level still away: real jump
                }
                if (Math.Abs(r.Level - a.Level) <= SpikeFilter.TolerancePct)
                {
                    k = m;
                    break;
                }
                peak = Math.Max(peak, r.Level);
                trough = Math.Min(trough, r.Level);
                if (peak - trough >= SpikeFilter.JumpPct)
                {
                    k = m;
                    break;
                }
            }
            if (k < 0)
            {
                i++;
                continue;
            }
            for (int m = i + 1; m < k; m++)
            {
                doomed.Add(hist[m].Ts);
            }
            i = k;
        }
        return doomed;
    }

    /// <summary>DB half of <see cref="ScrubGlitches"/> on an explicit
    /// connection (testable against a scratch database).</summary>
    internal static int DeleteSamples(SqliteConnection conn, string handle, IReadOnlyList<long> tss)
    {
        try
        {
            int n = 0;
            foreach (var ts in tss)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM samples WHERE handle = $h AND ts = $t";
                cmd.Parameters.AddWithValue("$h", handle);
                cmd.Parameters.AddWithValue("$t", ts);
                n += cmd.ExecuteNonQuery();
            }
            return n;
        }
        catch (Exception e)
        {
            Log.Error($"history: glitch scrub failed ({handle})", e);
            return 0;
        }
    }

    private static readonly SpikeFilter _spikes = new();

    public static bool Ready()
    {
        lock (DbLock)
        {
            return _db is not null;
        }
    }

    /// <summary>Flush hook for app exit (WAL checkpoints on close).</summary>
    public static void Close()
    {
        lock (Sync)
        {
            _estimates.Clear();
        }
        lock (DbLock)
        {
            _db?.Dispose();
            _db = null;
        }
    }

    /// <summary>The sample to record for a device state. A disconnected
    /// device cannot report a live level — the store parrots its last read,
    /// which may be the very glitch the spike hold below is about to catch
    /// (58% live → glitch-100 at power-off → disconnected heartbeats repeat
    /// 100 until the 60s grace expires and commits the whole plateau as a
    /// "real swap", seen live 2026-09-10 05:04). Disconnected rows carry the
    /// series' trusted level instead, so the hold sees the true fall-back
    /// and drops the glitch segment outright.</summary>
    internal static Sample CoalesceSample(Sample incoming, Sample? lastTrusted)
    {
        if (incoming.Connected || lastTrusted is null)
        {
            return incoming;
        }
        return incoming with { Level = lastTrusted.GetValueOrDefault().Level, Charging = false };
    }

    /// <summary>Called on the watcher thread after every parse pass. The
    /// caller passes its single config read; the disabled path must still
    /// run — it is what retires the prediction cache, so tray/hover/eta
    /// stop showing stale estimates once recording is off.</summary>
    public static void Record(DeviceStore devices, bool recordEnabled)
    {
        if (!recordEnabled)
        {
            lock (Sync)
            {
                if (_estimates.Count > 0)
                {
                    _estimates.Clear();
                }
            }
            return;
        }

        SqliteConnection? conn;
        lock (DbLock)
        {
            conn = _db;
        }
        if (conn is null)
        {
            return;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var map = devices.Snapshot();
        var dirty = new HashSet<string>();

        lock (Sync)
        {
            // The single connection is shared with UI-thread reads: all
            // commands serialize on DbLock (Microsoft.Data.Sqlite allows one
            // open command per connection).
            lock (DbLock)
            {
                var present = new HashSet<string>();
                foreach (var d in map.Values)
                {
                    present.Add(d.Handle);
                    if (!_series.TryGetValue(d.Handle, out var hist))
                    {
                        hist = new List<Sample>();
                        _series[d.Handle] = hist;
                    }
                    bool need = hist.Count == 0;
                    var s = CoalesceSample(
                        new Sample(now, d.BatteryPercentage, d.IsCharging, d.IsConnected),
                        hist.Count > 0 ? hist[^1] : null);
                    if (!need)
                    {
                        var last = hist[^1];
                        need = last.Charging != s.Charging
                            || last.Connected != s.Connected
                            || last.Level != s.Level
                            || now - last.Ts >= HeartbeatSecs;
                    }
                    if (need)
                    {
                        dirty.Add(d.Handle);
                        // Spike guard: a suspicious jump is held out of the DB
                        // for 60s (see SpikeFilter) — a fall-back within the
                        // window drops the segment as a reporting glitch,
                        // expiry commits it as a real battery swap/reconnect.
                        foreach (var w in _spikes.Admit(d.Handle, s,
                            hist.Count > 0 ? hist[^1].Level : s.Level))
                        {
                            InsertSample(conn, d.Handle, w);
                            hist.Add(w);
                        }
                    }
                    UpsertDevice(conn, d.Handle, d.Name, now, d.Transport);
                    _names[d.Handle] = d.Name;
                    _sources[d.Handle] = d.Transport;
                }

                // Devices that vanished since the last pass: record the disconnect
                // once, so a power-save shutdown shows up as a gap, never as usage.
                List<string> gone = new();
                foreach (var (h, was) in _seen)
                {
                    if (was && !present.Contains(h))
                    {
                        gone.Add(h);
                    }
                }
                foreach (var h in gone)
                {
                    // An unconfirmed spike hold dies with the device: record the
                    // disconnect from the last written sample (the pre-jump
                    // level), never from the bogus value.
                    _spikes.Reset(h);
                    if (!_series.TryGetValue(h, out var hist) || hist.Count == 0)
                    {
                        // _seen without a series: MergeAlias folded this handle
                        // away mid-flight. Skip rather than fabricate a 0%
                        // disconnect row — the merged (serial-keyed) series
                        // carries the device state now.
                        Log.Info($"razer-taskbar: {h}: disconnect skipped, series merged away");
                        _seen[h] = false;
                        continue;
                    }
                    var s = new Sample(now, hist[^1].Level, false, false);
                    InsertSample(conn, h, s);
                    hist.Add(s);
                    _seen[h] = false;
                }
                foreach (var h in present)
                {
                    _seen[h] = true;
                }
            }

            // Refresh the estimate cache incrementally: only series that grew
            // this pass recompute (prediction is a pure function of the
            // series); connected tails without an entry fill on first sight.
            // The old full clear-and-recompute made every EstimateFor — the
            // per-second widget/tray paints and the 120ms hover tick — wait
            // behind an O(all-history) recompute for ALL devices, each pass.
            // Prediction and anchor run on the rebound-deflated series (see
            // ReboundFilter): a relaxation bump must neither inflate levelNow
            // nor restart the display anchor mid-bump.
            foreach (var h in dirty)
            {
                RecomputeEstimateLocked(h);
            }
            foreach (var h in _estimates.Keys.ToList())
            {
                if (!_series.TryGetValue(h, out var hist) || hist.Count == 0 || !hist[^1].Connected)
                {
                    _estimates.Remove(h);
                }
            }
            foreach (var (h, hist) in _series)
            {
                if (!_estimates.ContainsKey(h) && hist.Count > 0 && hist[^1].Connected)
                {
                    RecomputeEstimateLocked(h);
                }
            }
        }
    }

    /// <summary>Recompute (or retire) one device's cached estimate from its
    /// current series. Caller holds <see cref="Sync"/>. The estimate exists
    /// exactly while the series tail is connected and the model yields one.</summary>
    private static void RecomputeEstimateLocked(string handle)
    {
        if (!_series.TryGetValue(handle, out var hist)
            || hist.Count == 0
            || !hist[^1].Connected)
        {
            _estimates.Remove(handle);
            return;
        }
        var eff = ReboundFilter.Deflate(hist);
        if (PredictDeflated(eff, eff[^1].Level, eff[^1].Charging) is not { } e)
        {
            _estimates.Remove(handle);
            return;
        }
        _estimates[handle] = (e, LevelAnchorSecs(eff));
    }

    /// <summary>Cached prediction for a device (UI threads; cheap
    /// lock-and-copy), adjusted by the pseudo countdown (see
    /// <see cref="PseudoAdjust"/>). The prediction and its anchor were
    /// computed on the rebound-deflated series at record time.</summary>
    public static Estimate? EstimateFor(string handle)
    {
        lock (Sync)
        {
            return _estimates.TryGetValue(handle, out var cached)
                ? PseudoAdjust(cached.Est, cached.AnchorSecs, DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                : null;
        }
    }

    /// <summary>Start of the trailing run of connected samples at the
    /// current level — the moment the device "updated to" its current
    /// reading. A level change or a disconnect/reconnect restarts the run.
    /// Pass the rebound-deflated series (see <see cref="ReboundFilter.Deflate"/>)
    /// so a relaxation bump neither restarts nor extends the anchor.</summary>
    public static long LevelAnchorSecs(IReadOnlyList<Sample> hist)
    {
        if (hist.Count == 0)
        {
            return 0;
        }
        var last = hist[^1];
        int i = hist.Count - 1;
        while (i > 0)
        {
            var p = hist[i - 1];
            if (p.Level != last.Level || !p.Connected)
            {
                break;
            }
            i--;
        }
        return hist[i].Ts;
    }

    /// <summary>Pseudo estimated usage time: between level changes the raw
    /// cycle prediction sits nearly static, so the displayed remaining is
    /// anchored at the current level's start and shrinks in real time —
    /// predicted minus elapsed, floored at zero. Every level update
    /// re-anchors, restarting the countdown from a fresh prediction.</summary>
    public static Estimate PseudoAdjust(Estimate e, long anchorSecs, long nowSecs)
        => e with { Secs = Math.Max(e.Secs - Math.Max(nowSecs - anchorSecs, 0), 0) };

    /// <summary>(handle, name) roster for the history page device picker.</summary>
    public static List<(string Handle, string Name)> ListDevices()
    {
        lock (Sync)
        {
            var v = new List<(string, string)>(_names.Count);
            foreach (var (h, n) in _names)
            {
                v.Add((h, n));
            }
            v.Sort((a, b) => string.CompareOrdinal(a.Item2.ToLowerInvariant(), b.Item2.ToLowerInvariant()));
            return v;
        }
    }

    /// <summary>The link a device's reading last arrived on, as persisted in
    /// battery.db — null when the device has no stored row yet. The live
    /// value rides on <see cref="RazerDevice.Transport"/>; this one is what
    /// survives a restart.</summary>
    public static BatteryTransport? SavedTransport(string handle)
    {
        lock (Sync)
        {
            return _sources.TryGetValue(handle, out var t) ? t : null;
        }
    }

    /// <summary>Device name persisted in battery.db for `handle` — null when
    /// the device has no stored row yet. The HID source reads it to name a
    /// combo dongle's keyboard slot without re-reading the Synapse log.</summary>
    public static string? SavedName(string handle)
    {
        lock (Sync)
        {
            return _names.TryGetValue(handle, out var n) ? n : null;
        }
    }

    /// <summary>Raw samples for the history page chart/list (short blocking query, WAL read).</summary>
    public static List<Sample> SamplesInRange(string handle, long sinceTs)
    {
        var outList = new List<Sample>();
        SqliteConnection? conn;
        lock (DbLock)
        {
            conn = _db;
        }
        if (conn is null)
        {
            return outList;
        }
        try
        {
            // The connection is shared with the watcher thread's writes:
            // commands serialize on DbLock (one open command per connection).
            lock (DbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT ts, level, charging, connected FROM samples WHERE handle = $h AND ts >= $t ORDER BY ts";
                cmd.Parameters.AddWithValue("$h", handle);
                cmd.Parameters.AddWithValue("$t", sinceTs);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    outList.Add(new Sample(
                        reader.GetInt64(0),
                        (int)Math.Clamp(reader.GetInt64(1), 0L, 100L),
                        reader.GetInt64(2) != 0,
                        reader.GetInt64(3) != 0));
                }
            }
        }
        catch (Exception e)
        {
            // Read failure: return what we have (Rust swallows too) — but a
            // corrupt db must not pass for "no data yet" without a trace.
            Log.Error("battery.db range read failed", e);
        }
        return outList;
    }

    private static SqliteConnection OpenDb()
    {
        var path = ConfigService.DbPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        Exec(conn, "PRAGMA journal_mode=WAL");
        Exec(conn, "PRAGMA synchronous=NORMAL");
        Exec(conn, "PRAGMA busy_timeout=5000");
        EnsureTables(conn);
        return conn;
    }

    internal static void EnsureTables(SqliteConnection conn)
    {
        Exec(conn,
            "CREATE TABLE IF NOT EXISTS devices(" +
            "handle TEXT PRIMARY KEY, name TEXT NOT NULL, " +
            "first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL, " +
            "source TEXT NOT NULL DEFAULT '');" +
            "CREATE TABLE IF NOT EXISTS samples(" +
            "handle TEXT NOT NULL, ts INTEGER NOT NULL, level INTEGER NOT NULL, " +
            "charging INTEGER NOT NULL, connected INTEGER NOT NULL, " +
            "PRIMARY KEY(handle, ts)) WITHOUT ROWID;");
        // Databases written before the source column existed: add it rather
        // than recreating (the table carries every device's first/last seen).
        if (!HasColumn(conn, "devices", "source"))
        {
            Exec(conn, "ALTER TABLE devices ADD COLUMN source TEXT NOT NULL DEFAULT ''");
        }
    }

    /// <summary>True when `table` already carries `column` (the migration
    /// probe: CREATE TABLE IF NOT EXISTS is a no-op on an existing table, so
    /// added columns need their own step).</summary>
    private static bool HasColumn(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info($t)";
        cmd.Parameters.AddWithValue("$t", table);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(0), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Persisted devices.source text → transport. A row written
    /// before the column existed (or by a build that predates the transport)
    /// reads back empty, and an unknown value is no better: the log source is
    /// the honest default for both.</summary>
    internal static BatteryTransport ParseSavedSource(string? text)
        => Enum.TryParse(text, out BatteryTransport t) ? t : BatteryTransport.Log;

    /// <summary>Upsert one device row, recording the link its reading arrived
    /// on (<see cref="BatteryTransport"/>): the source that actually answered
    /// is part of the device's durable state, so it survives a restart and
    /// says where to look when a later read fails. Internal for tests.</summary>
    internal static void UpsertDevice(
        SqliteConnection conn, string handle, string name, long ts, BatteryTransport transport)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO devices(handle, name, first_seen, last_seen, source) VALUES($h, $n, $t, $t, $s) " +
            "ON CONFLICT(handle) DO UPDATE SET name = excluded.name, last_seen = excluded.last_seen, " +
            "source = excluded.source";
        cmd.Parameters.AddWithValue("$h", handle);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$t", ts);
        cmd.Parameters.AddWithValue("$s", transport.ToString());
        cmd.ExecuteNonQuery();
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void Exec(SqliteConnection conn, string sql, params (string Name, string Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        cmd.ExecuteNonQuery();
    }

    private static void LoadSeries(SqliteConnection conn)
    {
        var series = new Dictionary<string, List<Sample>>();
        var names = new Dictionary<string, string>();
        var sources = new Dictionary<string, BatteryTransport>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT handle, name, source FROM devices";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var h = reader.GetString(0);
                names[h] = reader.GetString(1);
                sources[h] = ParseSavedSource(reader.IsDBNull(2) ? null : reader.GetString(2));
            }
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT handle, ts, level, charging, connected FROM samples ORDER BY handle, ts";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var h = reader.GetString(0);
                var s = new Sample(
                    reader.GetInt64(1),
                    (int)Math.Clamp(reader.GetInt64(2), 0L, 100L),
                    reader.GetInt64(3) != 0,
                    reader.GetInt64(4) != 0);
                if (!series.TryGetValue(h, out var hist))
                {
                    hist = new List<Sample>();
                    series[h] = hist;
                }
                hist.Add(s);
            }
        }
        lock (Sync)
        {
            _series = series;
            _names = names;
            _sources = sources;
        }
    }

    private static void InsertSample(SqliteConnection conn, string handle, Sample s)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO samples(handle, ts, level, charging, connected) VALUES($h, $t, $l, $c, $k)";
        cmd.Parameters.AddWithValue("$h", handle);
        cmd.Parameters.AddWithValue("$t", s.Ts);
        cmd.Parameters.AddWithValue("$l", s.Level);
        cmd.Parameters.AddWithValue("$c", s.Charging ? 1 : 0);
        cmd.Parameters.AddWithValue("$k", s.Connected ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    private sealed class OpenRun
    {
        public bool Charging;
        public int Start;
        /// <summary>Last sample folded into the run (differs from the cursor after a glitch skip).</summary>
        public int Last;
        public long Active;
        public int Moved;
        /// <summary>The run opened on a battery swap (see <see cref="Span.SwapStart"/>).</summary>
        public bool Swapped;
    }

    /// <summary>Split a sample series into discharge cycles and charge sessions.
    /// An open run at the end is kept (a real, still-growing observation).
    /// Level movement is accumulated per counted interval (connected + short
    /// gap) rather than taken from the endpoints.</summary>
    /// <summary>Fold reporting flickers into the run they interrupt: a short
    /// disconnected stretch bounded by connected samples on both sides is an
    /// enumeration hiccup, not a power-off — the field DB holds ~470 bounded
    /// dropouts ≤ 5 min (spans 14 s … 5 min, almost always a single row)
    /// against 500+ genuine absences > 2 h. Bridged rows carry the run's
    /// charging flag, so the chart line, the off bands and the session spans
    /// all treat the device as continuously present. Longer or unbounded
    /// dropouts stay untouched: they are real absences and keep their band
    /// and session split. In place, before Deflate/ComputeSpans/render.</summary>
    public static void BridgeDropouts(List<Sample> s)
    {
        const int maxRows = 3;
        const long maxSpanSecs = 5 * 60;
        int i = 1;
        while (i < s.Count)
        {
            if (s[i].Connected)
            {
                i++;
                continue;
            }
            int j = i;
            while (j < s.Count && !s[j].Connected)
            {
                j++;
            }
            if (j < s.Count && j - i <= maxRows
                && s[i - 1].Connected
                && s[j].Ts - s[i - 1].Ts <= maxSpanSecs)
            {
                for (int k = i; k < j; k++)
                {
                    s[k] = s[k] with { Charging = s[i - 1].Charging, Connected = true };
                }
            }
            i = j;
        }
    }

    public static (List<Span> Discharge, List<Span> Charge) ComputeSpans(IReadOnlyList<Sample> samples)
    {
        var discharge = new List<Span>();
        var charge = new List<Span>();
        OpenRun? open = null;

        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (open is not null && open.Charging != s.Charging)
            {
                // Charge state flipped between open.Last and i: close at
                // open.Last — the boundary interval belongs to neither run.
                CloseSpan(samples, open.Charging, open.Start, open.Active, open.Moved, open.Last, open.Swapped, discharge, charge);
                open = new OpenRun { Charging = s.Charging, Start = i, Last = i };
            }
            else if (open is not null)
            {
                var prev = samples[open.Last];
                long gap = s.Ts - prev.Ts;
                int rise = s.Level - prev.Level;
                // Battery swap: a big jump up while discharging. If the very
                // next sample drops back below the pre-jump level, the jump
                // was a reporting glitch — skip this sample instead.
                bool jump = !s.Charging && rise >= SwapJumpPct;
                bool glitch = jump && i + 1 < samples.Count && samples[i + 1].Level < prev.Level;
                if (glitch)
                {
                    continue;
                }
                if (gap >= 0 && gap <= GapBreakSecs && s.Connected)
                {
                    open.Active += gap;
                    open.Moved += s.Charging ? Math.Max(rise, 0) : Math.Max(-rise, 0);
                }
                if (jump)
                {
                    // Close at the pre-swap sample; the new battery starts a
                    // fresh cycle at the jumped-up level (flagged as such —
                    // for a rechargeable device this is a swap too, e.g. a
                    // hot-swapped pack, and it is never a charge session).
                    CloseSpan(samples, open.Charging, open.Start, open.Active, open.Moved, open.Last, open.Swapped, discharge, charge);
                    open = new OpenRun { Charging = s.Charging, Start = i, Last = i, Swapped = true };
                }
                else if (!s.Charging && s.Level == 0)
                {
                    // Battery empty: the cycle ends here even if charging
                    // never starts (device powered off dead).
                    CloseSpan(samples, open.Charging, open.Start, open.Active, open.Moved, i, open.Swapped, discharge, charge);
                    open = null;
                }
                else
                {
                    open.Last = i;
                }
            }
            else
            {
                open = new OpenRun { Charging = s.Charging, Start = i, Last = i };
            }
        }
        if (open is not null)
        {
            CloseSpan(samples, open.Charging, open.Start, open.Active, open.Moved, open.Last, open.Swapped, discharge, charge);
        }
        return (discharge, charge);
    }

    private static void CloseSpan(
        IReadOnlyList<Sample> samples,
        bool charging,
        int startIdx,
        long active,
        int moved,
        int endIdx,
        bool swapStart,
        List<Span> discharge,
        List<Span> charge)
    {
        var s0 = samples[startIdx];
        var s1 = samples[endIdx];
        var span = new Span(s0.Ts, s1.Ts, active, s0.Level, s1.Level, moved, swapStart && !charging);
        if (charging)
        {
            charge.Add(span);
        }
        else
        {
            discharge.Add(span);
        }
    }

    /// <summary>EWMA recency weight for a span/session ending at <paramref
    /// name="ts"/> relative to the newest observation: 1.0 now, 0.5 after
    /// HalfLifeDays, effectively zero past MaxAgeDays.</summary>
    internal static double RecencyWeight(long newestTs, long ts)
        => Math.Pow(0.5, Math.Max(newestTs - ts, 0L) / 86400.0 / HalfLifeDays);

    /// <summary>Weighted "active hours per percent" over spans: each span is
    /// weighted by its age in days (EWMA, see <see cref="RecencyWeight"/>),
    /// spans beyond MaxAgeDays are ignored, and spans below the drop/activity
    /// floors are skipped — weight follows time only, so a junk span neither
    /// dilutes nor consumes anything.</summary>
    internal static double? WeightedHoursPerPct(IReadOnlyList<Span> spans)
    {
        if (spans.Count == 0)
        {
            return null;
        }
        var sorted = spans.OrderBy(s => -s.EndTs).ToList(); // stable, newest first
        long newest = sorted[0].EndTs;
        double wHours = 0.0;
        double wPct = 0.0;
        foreach (var s in sorted)
        {
            if ((newest - s.EndTs) / 86400.0 > MaxAgeDays)
            {
                break; // everything after is older still
            }
            if (!s.Qualifies(MinSpanDropPct, MinSpanActiveSecs))
            {
                continue;
            }
            double w = RecencyWeight(newest, s.EndTs);
            wHours += w * s.ActiveSecs / 3600.0;
            wPct += w * s.MovedPct;
        }
        return wPct <= 0.0 ? null : wHours / wPct;
    }

    /// <summary>Active hours and percent points accumulated by the open
    /// current session (trailing same-mode connected run, short gaps only),
    /// or null while it is too small to trust as a rate observation.</summary>
    internal static (double Hours, double Pct)? CurrentSessionProgress(IReadOnlyList<Sample> samples, bool charging)
    {
        int i = samples.Count - 1;
        if (i < 0 || samples[i].Charging != charging || !samples[i].Connected)
        {
            return null;
        }
        double hours = 0.0;
        double pct = 0.0;
        while (i > 0)
        {
            var a = samples[i - 1];
            var b = samples[i];
            if (a.Charging != charging || !b.Connected || b.Ts - a.Ts > GapBreakSecs)
            {
                break;
            }
            int delta = charging ? b.Level - a.Level : a.Level - b.Level;
            if (delta > 0)
            {
                hours += (b.Ts - a.Ts) / 3600.0;
                pct += delta;
            }
            i -= 1;
        }
        if (pct < CurSessionMinPct || hours * 3600.0 < CurSessionMinActiveSecs)
        {
            return null;
        }
        return (hours, pct);
    }

    /// <summary>Historical pooled rate blended with the current session's own
    /// rate: f = pct/(pct + BlendKPct), so the blend shifts toward what the
    /// device is doing right now as the session accumulates evidence. Either
    /// side alone is returned when the other is missing.</summary>
    internal static double? BlendedRate(IReadOnlyList<Sample> samples, IReadOnlyList<Span> spans, bool charging)
    {
        double? hist = WeightedHoursPerPct(spans);
        var cur = CurrentSessionProgress(samples, charging);
        if (cur is null)
        {
            return hist;
        }
        double curRate = cur.Value.Hours / cur.Value.Pct;
        if (hist is null)
        {
            return curRate;
        }
        double f = cur.Value.Pct / (cur.Value.Pct + BlendKPct);
        return (1.0 - f) * hist.Value + f * curRate;
    }

    private static double? PctTimesRate(double? hoursPerPct, int pct)
        => hoursPerPct is null ? null : pct * hoursPerPct.Value * 3600.0;

    /// <summary>Fallback for fresh installs (no usable cycle yet): drain/charge
    /// rate over the trailing InstantWindowSecs of connected samples.</summary>
    private static double? InstantRate(IReadOnlyList<Sample> samples, bool charging)
    {
        int i = samples.Count - 1;
        if (i < 0)
        {
            return null;
        }
        if (samples[i].Charging != charging || !samples[i].Connected)
        {
            return null;
        }
        long newest = samples[i].Ts;
        double hours = 0.0;
        double pct = 0.0;
        while (i > 0)
        {
            var a = samples[i - 1];
            var b = samples[i];
            if (a.Charging != charging
                || !b.Connected
                || b.Ts - a.Ts > GapBreakSecs
                || newest - a.Ts > InstantWindowSecs)
            {
                break;
            }
            int delta = charging ? b.Level - a.Level : a.Level - b.Level;
            if (delta > 0)
            {
                hours += (b.Ts - a.Ts) / 3600.0;
                pct += delta;
            }
            i -= 1;
        }
        if (hours * 3600.0 < InstantMinActiveSecs || pct < InstantMinDropPct)
        {
            return null;
        }
        return hours / pct;
    }

    /// <summary>Per-level charge transits: transit[k] = observed seconds to
    /// charge from k% to k+1%, EWMA-weighted across ALL charge sessions
    /// (recent sessions count more). Sessions do NOT need to reach 100% —
    /// top-up / dock habits simply cover fewer levels. Within a session only
    /// successive high-water levels are attributed, so idle time at a held
    /// level (docked at full, powered off) never pollutes the transits.</summary>
    internal static double?[] ChargeTransitProfile(IReadOnlyList<Sample> samples, List<Span> chargeSpans)
        => TransitProfile(samples, chargeSpans, toFull: true);

    /// <summary>Per-level discharge transits: transit[k] = observed seconds
    /// to drain from k+1% to k%. Partial cycles (swap-ended, or the device
    /// re-docked before empty) contribute exactly the levels they crossed.</summary>
    internal static double?[] DischargeTransitProfile(IReadOnlyList<Sample> samples, List<Span> dischargeSpans)
        => TransitProfile(samples, dischargeSpans, toFull: false);

    private static double?[] TransitProfile(IReadOnlyList<Sample> samples, List<Span> spans, bool toFull)
    {
        var transit = new double?[100];
        var participating = spans
            .Where(s => s.ActiveSecs >= MinSpanActiveSecs)
            .OrderBy(s => s.EndTs)
            .ToList();
        if (participating.Count == 0)
        {
            return transit;
        }
        long reference = participating[^1].EndTs;
        var acc = new double[100];
        var wsum = new double[100];
        int i = 0;
        foreach (var span in participating)
        {
            double w = RecencyWeight(reference, span.EndTs);
            // Walk the span's samples once (spans are disjoint in time, so a
            // single forward pointer suffices).
            while (i < samples.Count && samples[i].Ts < span.StartTs)
            {
                i++;
            }
            int extreme = toFull ? -1 : 101;
            long extremeTs = 0;
            for (int j = i; j < samples.Count && samples[j].Ts <= span.EndTs; j++)
            {
                int level = Math.Clamp(samples[j].Level, 0, 100);
                bool isNewExtreme = toFull ? level > extreme : level < extreme;
                if (!isNewExtreme)
                {
                    continue;
                }
                if (extreme is not (-1 or 101))
                {
                    // Spread the elapsed time evenly over the levels crossed
                    // since the previous extreme (per-level resolution comes
                    // from ordinary 1%-step reports; large multi-point jumps
                    // — sleep gaps — divide it uniformly).
                    double per = (samples[j].Ts - extremeTs) / (double)Math.Abs(level - extreme);
                    for (int k = Math.Min(extreme, level); k < Math.Max(extreme, level); k++)
                    {
                        acc[k] += w * per;
                        wsum[k] += w;
                    }
                }
                extreme = level;
                extremeTs = samples[j].Ts;
            }
        }
        for (int k = 0; k < 100; k++)
        {
            if (wsum[k] > 0.0)
            {
                transit[k] = acc[k] / wsum[k];
            }
        }
        return transit;
    }

    /// <summary>Seconds-to-full (charging) or seconds-to-empty (discharging)
    /// summed from per-level transits: from L the charge estimate needs
    /// transits L..99, the discharge estimate 0..L-1. Levels nobody observed
    /// are filled with <paramref name="fillPerPctSecs"/> (the tier-2 rate),
    /// so partial histories still yield a complete estimate — the result
    /// differs from a flat rate exactly where real shape data exists.
    /// Uncompletable sums (missing data, no fill) stay null.</summary>
    private static double?[] EndpointSums(double?[] transit, bool toFull, double? fillPerPctSecs)
    {
        var profile = new double?[101];
        for (int level = 0; level <= 100; level++)
        {
            double sum = 0.0;
            bool complete = true;
            if (toFull)
            {
                for (int k = level; k <= 99; k++)
                {
                    if (!TryAddTransit(ref sum, transit[k], fillPerPctSecs))
                    {
                        complete = false;
                        break;
                    }
                }
            }
            else
            {
                for (int k = 0; k < level; k++)
                {
                    if (!TryAddTransit(ref sum, transit[k], fillPerPctSecs))
                    {
                        complete = false;
                        break;
                    }
                }
            }
            profile[level] = complete ? sum : null;
        }
        return profile;
    }

    private static bool TryAddTransit(ref double sum, double? transit, double? fill)
    {
        if (transit is { } v)
        {
            sum += v;
            return true;
        }
        if (fill is { } f)
        {
            sum += f;
            return true;
        }
        return false;
    }

    /// <summary>Endpoint profile from completed coverage only (no rate fill):
    /// test/inspection form of <see cref="EndpointSums"/>.</summary>
    internal static double?[] ChargeTimeToFullProfile(IReadOnlyList<Sample> samples)
    {
        var (_, charge) = ComputeSpans(samples);
        return EndpointSums(ChargeTransitProfile(samples, charge), toFull: true, fillPerPctSecs: null);
    }

    internal static double?[] ChargeTimeToFullProfile(IReadOnlyList<Sample> samples, List<Span> chargeSpans, double? fillPerPctSecs)
        => EndpointSums(ChargeTransitProfile(samples, chargeSpans), toFull: true, fillPerPctSecs);

    internal static double?[] DischargeTimeToEmptyProfile(IReadOnlyList<Sample> samples)
    {
        var (discharge, _) = ComputeSpans(samples);
        return EndpointSums(DischargeTransitProfile(samples, discharge), toFull: false, fillPerPctSecs: null);
    }

    internal static double?[] DischargeTimeToEmptyProfile(IReadOnlyList<Sample> samples, List<Span> dischargeSpans, double? fillPerPctSecs)
        => EndpointSums(DischargeTransitProfile(samples, dischargeSpans), toFull: false, fillPerPctSecs);

    /// <summary>Look up a profile entry for <paramref name="level"/>: exact
    /// hit first, else the nearest entry within <see cref="ProfileLevelTolerance"/>
    /// points; a distance tie picks the lower level (the longer/conservative
    /// estimate).</summary>
    internal static double? LookupProfile(double?[] profile, int level)
    {
        level = Math.Clamp(level, 0, 100);
        if (profile[level] is { } exact)
        {
            return exact;
        }
        for (int d = 1; d <= ProfileLevelTolerance; d++)
        {
            bool hasLower = level - d >= 0 && profile[level - d] is not null;
            bool hasUpper = level + d <= 100 && profile[level + d] is not null;
            if (hasLower)
            {
                return profile[level - d];
            }
            if (hasUpper)
            {
                return profile[level + d];
            }
        }
        return null;
    }

    /// <summary>Predict the usable time left (discharging) or time to full
    /// (charging) from the RAW sample series (the same shape
    /// <see cref="CycleStatsOf"/>/`<see cref="HealthStatsOf"/> take — the
    /// rebound deflation happens here, exactly once; Deflate is NOT
    /// idempotent and a double pass would re-clamp accepted
    /// recalibrations). Tier 1: per-level transit profile (nonlinear
    /// endpoint curve, partial sessions included, unobserved levels filled
    /// from tier 2). Tier 2: EWMA-weighted cycle rate blended with the
    /// current session. Tier 3: trailing-window instant rate (fresh
    /// installs).</summary>
    public static Estimate? Predict(IReadOnlyList<Sample> samples, int levelNow, bool chargingNow)
        => PredictDeflated(ReboundFilter.Deflate(samples), levelNow, chargingNow);

    /// <summary>Prediction core on the already-deflated series
    /// (`levelNow`/`chargingNow` read from that series' tail by the
    /// record path). Not a public entry — raw series must go through
    /// <see cref="Predict"/> so deflation runs exactly once.</summary>
    private static Estimate? PredictDeflated(IReadOnlyList<Sample> samples, int levelNow, bool chargingNow)
    {
        var (discharge, charge) = ComputeSpans(samples);
        double? secs;
        if (chargingNow)
        {
            double? rate = BlendedRate(samples, charge, true);
            secs = LookupProfile(
                       ChargeTimeToFullProfile(samples, charge, PctTimesRate(rate, 1)), levelNow)
                ?? PctTimesRate(rate, 100 - levelNow);
        }
        else
        {
            double? rate = BlendedRate(samples, discharge, false);
            secs = LookupProfile(
                       DischargeTimeToEmptyProfile(samples, discharge, PctTimesRate(rate, 1)), levelNow)
                ?? PctTimesRate(rate, levelNow);
        }
        secs ??= PctTimesRate(InstantRate(samples, chargingNow), chargingNow ? 100 - levelNow : levelNow);
        return secs is null ? null : new Estimate((long)Math.Round(secs.Value), chargingNow);
    }

    /// <summary>Cycle count + weighted rates for the history page header.
    /// Cycles are EQUIVALENT FULL CYCLES: every complete charge-empty-
    /// recharge sequence counts as one (total qualifying discharge ÷ 100%),
    /// partial sessions accrue fractionally — five 20% outings between
    /// docks make one cycle, not five. Computed on the rebound-deflated
    /// series: a relaxation bump's fake consumption must not skew the
    /// pooled rates.</summary>
    public static CycleStats CycleStatsOf(IReadOnlyList<Sample> samples)
    {
        var (discharge, charge) = ComputeSpans(ReboundFilter.Deflate(samples));
        return CycleStatsOfSpans(discharge, charge);
    }

    /// <summary>Spans-based form of <see cref="CycleStatsOf"/> for callers
    /// that already split the (deflated) series — the page reuses one
    /// ComputeSpans pass for stats, health and the cycle list. Cycles sum
    /// the qualifying spans' moved percent (the <see cref="Span.Qualifies"/>
    /// floors keep a 2-point flicker from minting cycles) and divide by
    /// 100; off periods contribute nothing (they never move percent).</summary>
    public static CycleStats CycleStatsOfSpans(List<Span> discharge, List<Span> charge)
        => new(
            discharge.Where(s => s.Qualifies(MinSpanDropPct, MinSpanActiveSecs)).Sum(s => s.MovedPct) / 100.0,
            WeightedHoursPerPct(discharge),
            WeightedHoursPerPct(charge));

    /// <summary>Battery health / lifespan estimate for the history page, or
    /// null when there is not enough recorded data yet.
    ///
    /// Charge speed is the only clean capacity-fade proxy available from
    /// percentage samples: the charge current is set by the dock/cable (not
    /// by usage), and as capacity fades each percent holds less charge, so
    /// charge-hours-per-percent shrinks proportionally. Drain rate can't be
    /// used — it tracks usage, not health. SOH is the recent EWMA rate
    /// relative to the earliest recorded sessions (i.e. relative health, valid
    /// even for a battery that was already aged when recording started); the
    /// fade trend is a least-squares slope over session rates, extrapolated
    /// to the 80% end-of-life convention.</summary>
    public static HealthStats? HealthStatsOf(IReadOnlyList<Sample> samples)
    {
        var (_, charge) = ComputeSpans(ReboundFilter.Deflate(samples));
        return HealthStatsOfSpans(charge);
    }

    /// <summary>Spans-based form of <see cref="HealthStatsOf"/> (see
    /// <see cref="CycleStatsOfSpans"/>): takes the deflated charge sessions.</summary>
    public static HealthStats? HealthStatsOfSpans(List<Span> charge)
    {
        var qualified = charge
            .Where(s => s.Qualifies(MinSpanDropPct, MinSpanActiveSecs) && s.MovedPct >= HealthMinChargePct)
            .OrderBy(s => s.EndTs)
            .ToList();
        if (qualified.Count < HealthMinSpans
            || (qualified[^1].EndTs - qualified[0].EndTs) / 86400.0 < HealthMinWindowDays)
        {
            return null;
        }
        var pts = qualified
            .Select(s => (T: (double)(s.StartTs + s.EndTs) / 2.0, Rate: s.ActiveSecs / 3600.0 / s.MovedPct))
            .ToList();

        // "New battery" reference: the earliest sessions on record.
        double baseline = pts.Take(HealthMinSpans).Average(p => p.Rate);
        if (baseline <= 0.0)
        {
            return null;
        }

        // Recent rate: EWMA across all sessions (recent cycles dominate).
        long newest = qualified[^1].EndTs;
        double wSum = 0.0, wRate = 0.0;
        for (int i = 0; i < pts.Count; i++)
        {
            double w = RecencyWeight(newest, qualified[i].EndTs);
            wSum += w;
            wRate += w * pts[i].Rate;
        }
        double soh = Math.Clamp(100.0 * (wRate / wSum) / baseline, 5.0, 100.0);

        // Fade trend: least-squares slope of rate vs time (a shrinking rate
        // means fading capacity), expressed as % of baseline lost per month.
        double meanT = pts.Average(p => p.T);
        double meanR = pts.Average(p => p.Rate);
        double num = 0.0, den = 0.0;
        foreach (var p in pts)
        {
            num += (p.T - meanT) * (p.Rate - meanR);
            den += (p.T - meanT) * (p.T - meanT);
        }
        double fadePerMonth = den > 0.0 && num < 0.0
            ? Math.Min(-num / den * (86400.0 * 30.0) / baseline * 100.0, HealthMaxFadePerMonth)
            : 0.0;

        double? monthsToEol = soh <= HealthEolPct ? 0.0
            : fadePerMonth >= HealthStableFadePerMonth ? (soh - HealthEolPct) / fadePerMonth
            : null;
        return new HealthStats(soh, fadePerMonth, monthsToEol);
    }

    public static string FormatDuration(long totalSecs)
    {
        long mins = Math.Max(totalSecs / 60, 0);
        if (mins < 1)
        {
            return "<1m";
        }
        if (mins < 60)
        {
            return $"{mins}m";
        }
        if (mins < 60 * 24 * 10)
        {
            long h = mins / 60;
            long m = mins % 60;
            return m == 0 ? $"{h}h" : $"{h}h{m:D2}m";
        }
        long d = mins / (60 * 24);
        long hh = (mins % (60 * 24)) / 60;
        return $"{d}d{hh}h";
    }

    /// <summary>Compact text for hover panel / widget: "~3h25m" left, "+1h10m" to full.</summary>
    public static string FormatEstimateCompact(Estimate e)
        => e.Charging ? $"+{FormatDuration(e.Secs)}" : $"~{FormatDuration(e.Secs)}";

    /// <summary>Bare duration for the widget's second row ("3h25m"): the row's
    /// status icon already conveys charging, so no ~/+ prefix.</summary>
    public static string FormatEstimatePlain(Estimate e) => FormatDuration(e.Secs);

    /// <summary>Verbal text for the tray tooltip (localized sentence, neutral duration).</summary>
    public static string FormatEstimateVerbose(Estimate e)
    {
        var d = FormatDuration(e.Secs);
        return e.Charging
            ? I18n.Tr("full in {}").Replace("{}", d)
            : I18n.Tr("~{} left").Replace("{}", d);
    }
}
