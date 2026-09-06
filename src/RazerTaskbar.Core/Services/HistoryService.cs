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
//   before it is ever recorded, expiry commits it as a real swap.
// - Prediction: the newest 10 cycles count fully, the next 90 at half
//   weight, cycles beyond 100 are ignored.
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

    /// <summary>Newest cycles that count with full weight.</summary>
    private const int RecentFull = 10;

    /// <summary>Cycles older than this (counted from the newest backwards) are ignored.</summary>
    private const int ExtendedLimit = 100;

    /// <summary>Weight of cycles between RecentFull and ExtendedLimit.</summary>
    private const double ExtendedWeight = 0.5;

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
    /// <summary>handle → present in the last record pass (drives disconnect detection).</summary>
    private static Dictionary<string, bool> _seen = new();
    private static Dictionary<string, Estimate> _estimates = new();

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
            Console.Error.WriteLine($"razer-taskbar: battery.db open failed: {e.Message}");
            return;
        }
        try
        {
            LoadSeries(conn);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"razer-taskbar: battery.db load failed: {e.Message}");
            return;
        }
        lock (DbLock)
        {
            _db = conn;
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

    /// <summary>Called on the watcher thread after every parse pass.</summary>
    public static void Record(DeviceStore devices)
    {
        if (!ConfigService.Load().RecordBatteryHistory)
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

        lock (Sync)
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
                if (!need)
                {
                    var last = hist[^1];
                    need = last.Charging != d.IsCharging
                        || last.Connected != d.IsConnected
                        || last.Level != d.BatteryPercentage
                        || now - last.Ts >= HeartbeatSecs;
                }
                if (need)
                {
                    var s = new Sample(now, d.BatteryPercentage, d.IsCharging, d.IsConnected);
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
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "INSERT INTO devices(handle, name, first_seen, last_seen) VALUES($h, $n, $t, $t) " +
                        "ON CONFLICT(handle) DO UPDATE SET name = excluded.name, last_seen = excluded.last_seen";
                    cmd.Parameters.AddWithValue("$h", d.Handle);
                    cmd.Parameters.AddWithValue("$n", d.Name);
                    cmd.Parameters.AddWithValue("$t", now);
                    cmd.ExecuteNonQuery();
                }
                _names[d.Handle] = d.Name;
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
                int level = _series.TryGetValue(h, out var hist) && hist.Count > 0 ? hist[^1].Level : 0;
                var s = new Sample(now, level, false, false);
                InsertSample(conn, h, s);
                if (!_series.TryGetValue(h, out var hist2))
                {
                    hist2 = new List<Sample>();
                    _series[h] = hist2;
                }
                hist2.Add(s);
                _seen[h] = false;
            }
            foreach (var h in present)
            {
                _seen[h] = true;
            }

            // Refresh the estimate cache for every connected device.
            _estimates.Clear();
            foreach (var (h, hist) in _series)
            {
                if (hist.Count == 0 || !hist[^1].Connected)
                {
                    continue;
                }
                if (Predict(hist, hist[^1].Level, hist[^1].Charging) is { } e)
                {
                    _estimates[h] = e;
                }
            }
        }
    }

    /// <summary>Cached prediction for a device (UI threads; cheap
    /// lock-and-copy), adjusted by the pseudo countdown (see
    /// <see cref="PseudoAdjust"/>).</summary>
    public static Estimate? EstimateFor(string handle)
    {
        lock (Sync)
        {
            if (!_estimates.TryGetValue(handle, out var e))
            {
                return null;
            }
            if (_series.TryGetValue(handle, out var hist) && hist.Count > 0)
            {
                e = PseudoAdjust(e, LevelAnchorSecs(hist), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            return e;
        }
    }

    /// <summary>Start of the trailing run of connected samples at the
    /// current level — the moment the device "updated to" its current
    /// reading. A level change or a disconnect/reconnect restarts the run.</summary>
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
        catch (Exception)
        {
            // Read failure: return what we have (Rust swallows too).
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
        Exec(conn,
            "CREATE TABLE IF NOT EXISTS devices(" +
            "handle TEXT PRIMARY KEY, name TEXT NOT NULL, " +
            "first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS samples(" +
            "handle TEXT NOT NULL, ts INTEGER NOT NULL, level INTEGER NOT NULL, " +
            "charging INTEGER NOT NULL, connected INTEGER NOT NULL, " +
            "PRIMARY KEY(handle, ts)) WITHOUT ROWID;");
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void LoadSeries(SqliteConnection conn)
    {
        var series = new Dictionary<string, List<Sample>>();
        var names = new Dictionary<string, string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT handle, name FROM devices";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                names[reader.GetString(0)] = reader.GetString(1);
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
    }

    /// <summary>Split a sample series into discharge cycles and charge sessions.
    /// An open run at the end is kept (a real, still-growing observation).
    /// Level movement is accumulated per counted interval (connected + short
    /// gap) rather than taken from the endpoints.</summary>
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
                CloseSpan(samples, open.Charging, open.Start, open.Active, open.Moved, open.Last, discharge, charge);
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
                    // fresh cycle at the jumped-up level.
                    CloseSpan(samples, open.Charging, open.Start, open.Active, open.Moved, open.Last, discharge, charge);
                    open = new OpenRun { Charging = s.Charging, Start = i, Last = i };
                }
                else if (!s.Charging && s.Level == 0)
                {
                    // Battery empty: the cycle ends here even if charging
                    // never starts (device powered off dead).
                    CloseSpan(samples, open.Charging, open.Start, open.Active, open.Moved, i, discharge, charge);
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
            CloseSpan(samples, open.Charging, open.Start, open.Active, open.Moved, open.Last, discharge, charge);
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
        List<Span> discharge,
        List<Span> charge)
    {
        var s0 = samples[startIdx];
        var s1 = samples[endIdx];
        var span = new Span(s0.Ts, s1.Ts, active, s0.Level, s1.Level, moved);
        if (charging)
        {
            charge.Add(span);
        }
        else
        {
            discharge.Add(span);
        }
    }

    /// <summary>Weighted "active hours per percent" over spans, newest first:
    /// the newest RecentFull count fully, the next batch at ExtendedWeight,
    /// anything past ExtendedLimit is ignored. Spans below the drop/activity
    /// floors are skipped entirely.</summary>
    internal static double? WeightedHoursPerPct(IReadOnlyList<Span> spans)
    {
        var sorted = spans.OrderBy(s => -s.EndTs).ToList(); // stable, newest first
        double wHours = 0.0;
        double wPct = 0.0;
        for (int i = 0; i < sorted.Count; i++)
        {
            double w;
            if (i < RecentFull)
            {
                w = 1.0;
            }
            else if (i < ExtendedLimit)
            {
                w = ExtendedWeight;
            }
            else
            {
                break;
            }
            if (!sorted[i].Qualifies(MinSpanDropPct, MinSpanActiveSecs))
            {
                continue;
            }
            wHours += w * sorted[i].ActiveSecs / 3600.0;
            wPct += w * sorted[i].MovedPct;
        }
        return wPct <= 0.0 ? null : wHours / wPct;
    }

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

    /// <summary>Predict the usable time left (discharging) or time to full
    /// (charging) from the sample series plus the device's current state.</summary>
    public static Estimate? Predict(IReadOnlyList<Sample> samples, int levelNow, bool chargingNow)
    {
        var (discharge, charge) = ComputeSpans(samples);
        var spans = chargingNow ? charge : discharge;
        var rate = WeightedHoursPerPct(spans);
        if (rate is not null)
        {
            int left = chargingNow ? 100 - levelNow : levelNow;
            return new Estimate((long)Math.Round(left * rate.Value * 3600.0), chargingNow);
        }
        rate = InstantRate(samples, chargingNow);
        if (rate is null)
        {
            return null;
        }
        int leftFallback = chargingNow ? 100 - levelNow : levelNow;
        return new Estimate((long)Math.Round(leftFallback * rate.Value * 3600.0), chargingNow);
    }

    /// <summary>Cycle count + weighted rates for the history page header.</summary>
    public static CycleStats CycleStatsOf(IReadOnlyList<Sample> samples)
    {
        var (discharge, charge) = ComputeSpans(samples);
        return new CycleStats(
            discharge.Count(s => s.Qualifies(MinSpanDropPct, MinSpanActiveSecs)),
            WeightedHoursPerPct(discharge),
            WeightedHoursPerPct(charge));
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
