//! Battery history: SQLite sampling, usage-cycle computation, usage-time
//! prediction (`battery.db` lives next to settings.json, retained forever).
//!
//! - Sampling runs on the watcher thread after every parse pass. A point is
//!   written when (connected, charging, level) changes, plus a heartbeat so
//!   "still connected" stays fresh; the record interval setting controls how
//!   fast the watcher polls, so transitions are timestamped precisely.
//! - A discharge cycle spans across power-save shutdowns: a disconnected
//!   stretch (>30 min silence or `connected=0`) contributes NO active time
//!   but does NOT end the cycle — that is the "idle auto-off" semantics.
//! - A battery swap (swappable-battery devices) shows up as a large level
//!   JUMP while discharging (e.g. 20% → 80%): the cycle ends there and a
//!   fresh one starts at the new level. A jump that immediately falls back
//!   is treated as a reporting glitch and dropped.
//! - Discharge cycles and charge sessions are tracked separately, each with
//!   its own weighted "active hours per percent" rate. Level movement is
//!   accumulated per counted interval only, so level changes across silent
//!   gaps (PC asleep, dock charging overnight) never pollute a rate.
//! - Prediction: the newest `RECENT_FULL` cycles count fully, the next ones
//!   at `EXTENDED_WEIGHT`, cycles beyond `EXTENDED_LIMIT` are ignored
//!   (battery wear makes old cycles unrepresentative).

use std::collections::{HashMap, HashSet};
use std::sync::{LazyLock, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};

use rusqlite::{params, Connection};

use crate::watcher::DeviceMap;

/// Heartbeat while a device sits unchanged. Must stay well below
/// `GAP_BREAK_SECS`, which treats longer silence as off/unknown.
const HEARTBEAT_SECS: i64 = 15 * 60;
/// Silence longer than this is not counted as active time (PC sleep, silent
/// reconnects) but does not end the running cycle. The viewer chart uses the
/// same threshold to decide where to draw a gap band.
pub const GAP_BREAK_SECS: i64 = 30 * 60;
/// A level rise of at least this many points while discharging is a battery
/// swap (swappable-battery devices), not data noise.
const SWAP_JUMP_PCT: i32 = 30;
/// Newest cycles that count with full weight.
const RECENT_FULL: usize = 10;
/// Cycles older than this (counted from the newest backwards) are ignored.
const EXTENDED_LIMIT: usize = 100;
/// Weight of cycles between `RECENT_FULL` and `EXTENDED_LIMIT`.
const EXTENDED_WEIGHT: f64 = 0.5;
/// A cycle/session must move at least this many percent to be counted
/// (filters quick dock/undock blips).
const MIN_SPAN_DROP_PCT: i32 = 5;
/// …and must span at least this much active time (filters glitches).
const MIN_SPAN_ACTIVE_SECS: i64 = 60;
/// Fallback (no usable cycle yet): instant rate over the last window.
const INSTANT_WINDOW_SECS: i64 = 30 * 60;
const INSTANT_MIN_ACTIVE_SECS: i64 = 5 * 60;
const INSTANT_MIN_DROP_PCT: f64 = 1.0;

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Sample {
    pub ts: i64,
    pub level: u8,
    pub charging: bool,
    pub connected: bool,
}

/// One discharge cycle or charge session.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Span {
    pub start_ts: i64,
    pub end_ts: i64,
    /// Wall time the device was actually powered on (off/unknown excluded).
    pub active_secs: i64,
    pub level_start: u8,
    pub level_end: u8,
    /// Percent points moved during counted (active) intervals only — the
    /// denominator of the rate. Positive for both span kinds.
    pub moved_pct: i32,
}

impl Span {
    fn qualifies(&self) -> bool {
        self.moved_pct >= MIN_SPAN_DROP_PCT && self.active_secs >= MIN_SPAN_ACTIVE_SECS
    }
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Estimate {
    pub secs: i64,
    /// true = time until full; false = usable time left.
    pub charging: bool,
}

/// Aggregate stats for a sample series (viewer header).
#[derive(Debug, Clone, Copy, Default)]
pub struct CycleStats {
    pub cycles: usize,
    pub use_hours_per_pct: Option<f64>,
    pub charge_hours_per_pct: Option<f64>,
}

// UI threads (hover/tray/widget/viewer) only ever read ESTIMATES / query the
// DB behind this mutex; the watcher thread is the sole writer.
static DB: Mutex<Option<Connection>> = Mutex::new(None);
static SERIES: LazyLock<Mutex<HashMap<String, Vec<Sample>>>> =
    LazyLock::new(|| Mutex::new(HashMap::new()));
static NAMES: LazyLock<Mutex<HashMap<String, String>>> =
    LazyLock::new(|| Mutex::new(HashMap::new()));
/// handle → present in the last record pass (drives disconnect detection).
static SEEN: LazyLock<Mutex<HashMap<String, bool>>> = LazyLock::new(|| Mutex::new(HashMap::new()));
static ESTIMATES: LazyLock<Mutex<HashMap<String, Estimate>>> =
    LazyLock::new(|| Mutex::new(HashMap::new()));

/// Open `battery.db`, create tables, mirror existing rows into memory.
pub fn init() {
    let conn = match open_db() {
        Ok(c) => c,
        Err(e) => {
            eprintln!("razer-taskbar: battery.db open failed: {e}");
            return;
        }
    };
    if let Err(e) = load_series(&conn) {
        eprintln!("razer-taskbar: battery.db load failed: {e}");
        return;
    }
    *DB.lock().unwrap() = Some(conn);
}

pub fn ready() -> bool {
    DB.lock().map(|g| g.is_some()).unwrap_or(false)
}

/// Flush hook for WM_DESTROY (WAL checkpoints on close).
pub fn close() {
    *ESTIMATES.lock().unwrap() = HashMap::new();
    *DB.lock().unwrap() = None;
}

/// Called on the watcher thread after every parse pass.
pub fn record(devices: &Mutex<DeviceMap>) {
    if !crate::config::load().record_battery_history {
        let mut est = ESTIMATES.lock().unwrap();
        if !est.is_empty() {
            est.clear();
        }
        return;
    }
    let db = DB.lock().unwrap();
    let Some(conn) = db.as_ref() else { return };
    let now = unix_now();
    let map = devices.lock().unwrap();
    let mut series = SERIES.lock().unwrap();
    let mut names = NAMES.lock().unwrap();
    let mut seen = SEEN.lock().unwrap();

    let mut present: HashSet<&str> = HashSet::new();
    for d in map.values() {
        present.insert(d.handle.as_str());
        let hist = series.entry(d.handle.clone()).or_default();
        let need = match hist.last() {
            None => true,
            Some(s) => {
                s.charging != d.is_charging
                    || s.connected != d.is_connected
                    || s.level != d.battery_percentage
                    || now - s.ts >= HEARTBEAT_SECS
            }
        };
        if need {
            let s = Sample {
                ts: now,
                level: d.battery_percentage,
                charging: d.is_charging,
                connected: d.is_connected,
            };
            insert_sample(conn, &d.handle, &s);
            hist.push(s);
        }
        let _ = conn.execute(
            "INSERT INTO devices(handle, name, first_seen, last_seen) VALUES(?1, ?2, ?3, ?3)
             ON CONFLICT(handle) DO UPDATE SET name = excluded.name, last_seen = excluded.last_seen",
            params![d.handle, d.name, now],
        );
        names.insert(d.handle.clone(), d.name.clone());
    }
    // Devices that vanished since the last pass: record the disconnect once,
    // so a power-save shutdown shows up as a gap, never as usage time.
    let gone: Vec<String> = seen
        .iter()
        .filter(|(h, was)| **was && !present.contains(h.as_str()))
        .map(|(h, _)| h.clone())
        .collect();
    for h in gone {
        let last = series.get(&h).and_then(|v| v.last()).copied();
        let s = Sample {
            ts: now,
            level: last.map(|s| s.level).unwrap_or(0),
            charging: false,
            connected: false,
        };
        insert_sample(conn, &h, &s);
        series.entry(h.clone()).or_default().push(s);
        seen.insert(h, false);
    }
    for h in &present {
        seen.insert(h.to_string(), true);
    }
    drop(map);

    // Refresh the estimate cache for every connected device (the all-series
    // walk is cheap: transition + 15-min-heartbeat samples only).
    let mut estimates = ESTIMATES.lock().unwrap();
    estimates.clear();
    for (h, hist) in series.iter() {
        let Some(last) = hist.last() else { continue };
        if !last.connected {
            continue;
        }
        if let Some(e) = predict(hist, last.level, last.charging) {
            estimates.insert(h.clone(), e);
        }
    }
}

/// Cached prediction for a device (UI threads; cheap lock-and-copy).
pub fn estimate_for(handle: &str) -> Option<Estimate> {
    ESTIMATES.lock().ok()?.get(handle).copied()
}

/// (handle, name) roster for the viewer device picker.
pub fn list_devices() -> Vec<(String, String)> {
    NAMES.lock()
        .map(|n| {
            let mut v: Vec<_> = n.iter().map(|(h, n)| (h.clone(), n.clone())).collect();
            v.sort_by(|a, b| a.1.to_lowercase().cmp(&b.1.to_lowercase()));
            v
        })
        .unwrap_or_default()
}

/// Raw samples for the viewer chart/list (short blocking query, WAL read).
pub fn samples_in_range(handle: &str, since_ts: i64) -> Vec<Sample> {
    let db = DB.lock().unwrap();
    let Some(conn) = db.as_ref() else {
        return Vec::new();
    };
    let mut out = Vec::new();
    if let Ok(mut stmt) = conn.prepare(
        "SELECT ts, level, charging, connected FROM samples WHERE handle = ?1 AND ts >= ?2 ORDER BY ts",
    ) {
        if let Ok(rows) = stmt.query_map(params![handle, since_ts], |r| {
            Ok(Sample {
                ts: r.get(0)?,
                level: r.get::<_, i64>(1)?.clamp(0, 100) as u8,
                charging: r.get::<_, i64>(2)? != 0,
                connected: r.get::<_, i64>(3)? != 0,
            })
        }) {
            for row in rows.flatten() {
                out.push(row);
            }
        }
    }
    out
}

fn open_db() -> rusqlite::Result<Connection> {
    let path = crate::config::db_path();
    if let Some(parent) = path.parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    let conn = Connection::open(&path)?;
    conn.pragma_update(None, "journal_mode", "WAL")?;
    conn.pragma_update(None, "synchronous", "NORMAL")?;
    let _ = conn.busy_timeout(std::time::Duration::from_millis(5000));
    conn.execute_batch(
        "CREATE TABLE IF NOT EXISTS devices(
            handle TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            first_seen INTEGER NOT NULL,
            last_seen INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS samples(
            handle TEXT NOT NULL,
            ts INTEGER NOT NULL,
            level INTEGER NOT NULL,
            charging INTEGER NOT NULL,
            connected INTEGER NOT NULL,
            PRIMARY KEY(handle, ts)) WITHOUT ROWID;",
    )?;
    Ok(conn)
}

fn load_series(conn: &Connection) -> rusqlite::Result<()> {
    let mut series: HashMap<String, Vec<Sample>> = HashMap::new();
    let mut names: HashMap<String, String> = HashMap::new();
    {
        let mut stmt = conn.prepare("SELECT handle, name FROM devices")?;
        let rows = stmt.query_map([], |r| Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?)))?;
        for row in rows {
            let (h, n) = row?;
            names.insert(h, n);
        }
    }
    {
        let mut stmt = conn.prepare(
            "SELECT handle, ts, level, charging, connected FROM samples ORDER BY handle, ts",
        )?;
        let rows = stmt.query_map([], |r| {
            Ok((
                r.get::<_, String>(0)?,
                r.get::<_, i64>(1)?,
                r.get::<_, i64>(2)?.clamp(0, 100) as u8,
                r.get::<_, i64>(3)? != 0,
                r.get::<_, i64>(4)? != 0,
            ))
        })?;
        for row in rows {
            let (h, ts, level, charging, connected) = row?;
            series
                .entry(h)
                .or_default()
                .push(Sample { ts, level, charging, connected });
        }
    }
    *SERIES.lock().unwrap() = series;
    *NAMES.lock().unwrap() = names;
    Ok(())
}

fn insert_sample(conn: &Connection, handle: &str, s: &Sample) {
    let _ = conn.execute(
        "INSERT OR REPLACE INTO samples(handle, ts, level, charging, connected) VALUES(?1, ?2, ?3, ?4, ?5)",
        params![handle, s.ts, s.level as i64, s.charging as i64, s.connected as i64],
    );
}

fn unix_now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

/// Split a sample series into discharge cycles and charge sessions. An open
/// run at the end is kept (a real, still-growing observation).
///
/// Level movement is accumulated per counted interval (connected + short
/// gap) rather than taken from the endpoints, so level changes across
/// silent/off stretches never pollute a rate. A large rise while
/// discharging closes the cycle (battery swap) unless the next sample falls
/// back below the pre-jump level (reporting glitch — dropped).
pub fn compute_spans(samples: &[Sample]) -> (Vec<Span>, Vec<Span>) {
    #[derive(Clone, Copy)]
    struct Open {
        charging: bool,
        start: usize,
        /// Last sample folded into the run (differs from the cursor after a
        /// glitch skip).
        last: usize,
        active: i64,
        moved: i32,
    }
    let mut discharge: Vec<Span> = Vec::new();
    let mut charge: Vec<Span> = Vec::new();
    let mut open: Option<Open> = None;

    for i in 0..samples.len() {
        let s = samples[i];
        match open {
            Some(o) if o.charging != s.charging => {
                // Charge state flipped between o.last and i: close at
                // o.last — the boundary interval belongs to neither run.
                close_span(samples, o.charging, o.start, o.active, o.moved, o.last, &mut discharge, &mut charge);
                open = Some(Open { charging: s.charging, start: i, last: i, active: 0, moved: 0 });
            }
            Some(o) => {
                let prev = samples[o.last];
                let gap = s.ts - prev.ts;
                let rise = s.level as i32 - prev.level as i32;
                // Battery swap: a big jump up while discharging. If the very
                // next sample drops back below the pre-jump level, the jump
                // was a reporting glitch — skip this sample instead.
                let jump = !s.charging && rise >= SWAP_JUMP_PCT;
                let glitch = jump
                    && i + 1 < samples.len()
                    && (samples[i + 1].level as i32) < prev.level as i32;
                if glitch {
                    continue;
                }
                let mut o = o;
                if gap >= 0 && gap <= GAP_BREAK_SECS && s.connected {
                    o.active += gap;
                    o.moved += if s.charging { rise.max(0) } else { (-rise).max(0) };
                }
                if jump {
                    // Close at the pre-swap sample; the new battery starts a
                    // fresh cycle at the jumped-up level.
                    close_span(samples, o.charging, o.start, o.active, o.moved, o.last, &mut discharge, &mut charge);
                    open = Some(Open { charging: s.charging, start: i, last: i, active: 0, moved: 0 });
                } else if !s.charging && s.level == 0 {
                    // Battery empty: the cycle ends here even if charging
                    // never starts (device powered off dead).
                    close_span(samples, o.charging, o.start, o.active, o.moved, i, &mut discharge, &mut charge);
                    open = None;
                } else {
                    o.last = i;
                    open = Some(o);
                }
            }
            None => {
                open = Some(Open { charging: s.charging, start: i, last: i, active: 0, moved: 0 });
            }
        }
    }
    if let Some(o) = open {
        close_span(samples, o.charging, o.start, o.active, o.moved, o.last, &mut discharge, &mut charge);
    }
    (discharge, charge)
}

fn close_span(
    samples: &[Sample],
    charging: bool,
    start_idx: usize,
    active: i64,
    moved: i32,
    end_idx: usize,
    discharge: &mut Vec<Span>,
    charge: &mut Vec<Span>,
) {
    let s0 = samples[start_idx];
    let s1 = samples[end_idx];
    let span = Span {
        start_ts: s0.ts,
        end_ts: s1.ts,
        active_secs: active,
        level_start: s0.level,
        level_end: s1.level,
        moved_pct: moved,
    };
    if charging {
        charge.push(span);
    } else {
        discharge.push(span);
    }
}

/// Weighted "active hours per percent" over spans, newest first: the newest
/// `RECENT_FULL` count fully, the next batch at `EXTENDED_WEIGHT`, anything
/// past `EXTENDED_LIMIT` is ignored. Spans below the drop/activity floors
/// are skipped entirely.
fn weighted_hours_per_pct(spans: &[Span]) -> Option<f64> {
    let mut sorted: Vec<&Span> = spans.iter().collect();
    sorted.sort_by_key(|s| std::cmp::Reverse(s.end_ts));
    let mut w_hours = 0.0f64;
    let mut w_pct = 0.0f64;
    for (i, s) in sorted.iter().enumerate() {
        let w = if i < RECENT_FULL {
            1.0
        } else if i < EXTENDED_LIMIT {
            EXTENDED_WEIGHT
        } else {
            break;
        };
        if !s.qualifies() {
            continue;
        }
        w_hours += w * s.active_secs as f64 / 3600.0;
        w_pct += w * s.moved_pct as f64;
    }
    if w_pct <= 0.0 {
        None
    } else {
        Some(w_hours / w_pct)
    }
}

/// Fallback for fresh installs (no usable cycle yet): drain/charge rate
/// over the trailing `INSTANT_WINDOW_SECS` of connected samples.
fn instant_rate(samples: &[Sample], charging: bool) -> Option<f64> {
    let mut i = samples.len().checked_sub(1)?;
    if samples[i].charging != charging || !samples[i].connected {
        return None;
    }
    let newest = samples[i].ts;
    let mut hours = 0.0f64;
    let mut pct = 0.0f64;
    while i > 0 {
        let a = &samples[i - 1];
        let b = &samples[i];
        if a.charging != charging
            || !b.connected
            || b.ts - a.ts > GAP_BREAK_SECS
            || newest - a.ts > INSTANT_WINDOW_SECS
        {
            break;
        }
        let delta = if charging {
            b.level as i32 - a.level as i32
        } else {
            a.level as i32 - b.level as i32
        };
        if delta > 0 {
            hours += (b.ts - a.ts) as f64 / 3600.0;
            pct += delta as f64;
        }
        i -= 1;
    }
    if (hours * 3600.0) < INSTANT_MIN_ACTIVE_SECS as f64 || pct < INSTANT_MIN_DROP_PCT {
        return None;
    }
    Some(hours / pct)
}

/// Predict the usable time left (discharging) or time to full (charging)
/// from the sample series plus the device's current state.
pub fn predict(samples: &[Sample], level_now: u8, charging_now: bool) -> Option<Estimate> {
    let (discharge, charge) = compute_spans(samples);
    let (spans, rate_fallback) = if charging_now {
        (&charge, true)
    } else {
        (&discharge, false)
    };
    if let Some(rate) = weighted_hours_per_pct(spans) {
        let left = if charging_now { 100 - level_now } else { level_now };
        let secs = (left as f64 * rate * 3600.0).round() as i64;
        return Some(Estimate { secs, charging: charging_now });
    }
    let rate = instant_rate(samples, rate_fallback)?;
    let left = if rate_fallback { 100 - level_now } else { level_now };
    let secs = (left as f64 * rate * 3600.0).round() as i64;
    Some(Estimate { secs, charging: rate_fallback })
}

/// Cycle count + weighted rates for the viewer header.
pub fn cycle_stats(samples: &[Sample]) -> CycleStats {
    let (discharge, charge) = compute_spans(samples);
    CycleStats {
        cycles: discharge.iter().filter(|s| s.qualifies()).count(),
        use_hours_per_pct: weighted_hours_per_pct(&discharge),
        charge_hours_per_pct: weighted_hours_per_pct(&charge),
    }
}

pub fn format_duration(total_secs: i64) -> String {
    let mins = (total_secs / 60).max(0);
    if mins < 1 {
        return "<1m".into();
    }
    if mins < 60 {
        return format!("{mins}m");
    }
    if mins < 60 * 24 * 10 {
        let h = mins / 60;
        let m = mins % 60;
        return if m == 0 { format!("{h}h") } else { format!("{h}h{m:02}m") };
    }
    let d = mins / (60 * 24);
    let h = (mins % (60 * 24)) / 60;
    format!("{d}d{h}h")
}

/// Compact text for hover panel / widget: "~3h25m" left, "+1h10m" to full.
pub fn format_estimate_compact(e: Estimate) -> String {
    if e.charging {
        format!("+{}", format_duration(e.secs))
    } else {
        format!("~{}", format_duration(e.secs))
    }
}

/// Verbal text for the tray tooltip.
pub fn format_estimate_verbose(e: Estimate) -> String {
    if e.charging {
        format!("full in {}", format_duration(e.secs))
    } else {
        format!("~{} left", format_duration(e.secs))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn s(ts: i64, level: u8, charging: bool, connected: bool) -> Sample {
        Sample { ts, level, charging, connected }
    }

    #[test]
    fn duration_formatting() {
        assert_eq!(format_duration(0), "<1m");
        assert_eq!(format_duration(30), "<1m");
        assert_eq!(format_duration(45 * 60), "45m");
        assert_eq!(format_duration(3 * 3600 + 25 * 60), "3h25m");
        assert_eq!(format_duration(3 * 3600), "3h");
        assert_eq!(format_duration(25 * 86400 + 4 * 3600), "25d4h");
    }

    #[test]
    fn estimate_text() {
        let e = Estimate { secs: 3 * 3600 + 25 * 60, charging: false };
        assert_eq!(format_estimate_compact(e), "~3h25m");
        assert_eq!(format_estimate_verbose(e), "~3h25m left");
        let c = Estimate { secs: 70 * 60, charging: true };
        assert_eq!(format_estimate_compact(c), "+1h10m");
        assert_eq!(format_estimate_verbose(c), "full in 1h10m");
    }

    #[test]
    fn discharge_then_charge_split() {
        // 900s steps = the 15-min heartbeat cadence, well under the 30-min
        // gap break.
        let samples = vec![
            s(0, 100, false, true),
            s(900, 95, false, true),
            s(1800, 90, false, true),
            s(1801, 90, true, true),
            s(2701, 95, true, true),
        ];
        let (dis, chg) = compute_spans(&samples);
        assert_eq!(dis.len(), 1);
        let d = dis[0];
        assert_eq!((d.start_ts, d.end_ts), (0, 1800));
        assert_eq!(d.active_secs, 1800);
        assert_eq!((d.level_start, d.level_end), (100, 90));
        assert_eq!(d.moved_pct, 10);
        assert_eq!(chg.len(), 1);
        let c = chg[0];
        // The boundary interval (1800→1801) belongs to neither run.
        assert_eq!((c.start_ts, c.end_ts), (1801, 2701));
        assert_eq!(c.active_secs, 900);
        assert_eq!(c.moved_pct, 5);
    }

    #[test]
    fn power_save_gap_excluded_but_cycle_continues() {
        // Device auto-offs at 901 (connected=0), wakes at 1801. The off
        // stretch counts no active time yet the cycle stays one span.
        let samples = vec![
            s(0, 100, false, true),
            s(900, 98, false, true),
            s(901, 98, false, false),
            s(1800, 97, false, false),
            s(1801, 97, false, true),
            s(2700, 95, false, true),
        ];
        let (dis, _chg) = compute_spans(&samples);
        assert_eq!(dis.len(), 1);
        let d = dis[0];
        assert_eq!((d.start_ts, d.end_ts), (0, 2700));
        // 0→900 on + 1800→1801 (wake tick) + 1801→2700 on; the off stretch
        // contributes nothing.
        assert_eq!(d.active_secs, 900 + 1 + 899);
        assert_eq!(d.moved_pct, 4);
        assert_eq!((d.level_start, d.level_end), (100, 95));
    }

    #[test]
    fn silent_gap_not_counted_as_active() {
        // No samples at all for >30 min (PC slept): time and the level move
        // across the gap are both excluded.
        let samples = vec![
            s(0, 100, false, true),
            s(900, 98, false, true),
            s(900 + 3600, 97, false, true),
            s(900 + 3600 + 900, 95, false, true),
        ];
        let (dis, _) = compute_spans(&samples);
        assert_eq!(dis.len(), 1);
        // Only the two short intervals count: 900 + 900, 2% + 2%.
        assert_eq!(dis[0].active_secs, 1800);
        assert_eq!(dis[0].moved_pct, 4);
    }

    #[test]
    fn battery_swap_jump_ends_cycle() {
        // Swappable-battery device: 15% → 80% while discharging closes the
        // cycle and starts a fresh one at the new level.
        let samples = vec![
            s(0, 20, false, true),
            s(900, 15, false, true),
            s(1800, 80, false, true),
            s(2700, 75, false, true),
        ];
        let (dis, _) = compute_spans(&samples);
        assert_eq!(dis.len(), 2);
        assert_eq!((dis[0].start_ts, dis[0].end_ts), (0, 900));
        assert_eq!((dis[0].level_start, dis[0].level_end), (20, 15));
        assert_eq!(dis[0].moved_pct, 5);
        assert_eq!((dis[1].start_ts, dis[1].end_ts), (1800, 2700));
        assert_eq!((dis[1].level_start, dis[1].level_end), (80, 75));
    }

    #[test]
    fn jump_that_falls_back_is_a_glitch() {
        // 48 → 100 → 47 one step later: the jump sample is dropped and the
        // run continues as if it never happened.
        let samples = vec![
            s(0, 50, false, true),
            s(900, 48, false, true),
            s(1800, 100, false, true),
            s(1801, 47, false, true),
        ];
        let (dis, _) = compute_spans(&samples);
        assert_eq!(dis.len(), 1);
        let d = dis[0];
        assert_eq!((d.level_start, d.level_end), (50, 47));
        // 0→900 counted; the glitch step is skipped, then 900→1801 counts.
        assert_eq!(d.active_secs, 900 + 901);
        assert_eq!(d.moved_pct, 3);
    }

    #[test]
    fn empty_battery_ends_cycle() {
        let samples = vec![
            s(0, 100, false, true),
            s(900, 50, false, true),
            s(901, 0, false, true),
            s(1800, 0, false, true),
        ];
        let (dis, _) = compute_spans(&samples);
        assert_eq!(dis.len(), 2);
        assert_eq!((dis[0].end_ts, dis[0].level_end, dis[0].moved_pct), (901, 0, 100));
        // Trailing zero-level run is a degenerate span (filtered by weight).
        assert_eq!(dis[1].moved_pct, 0);
    }

    #[test]
    fn open_cycle_kept_at_series_end() {
        let samples = vec![s(0, 100, false, true), s(900, 90, false, true)];
        let (dis, _) = compute_spans(&samples);
        assert_eq!(dis.len(), 1);
        assert_eq!(dis[0].active_secs, 900);
    }

    #[test]
    fn charge_across_silent_gap_does_not_inflate_rate() {
        // Dock charging overnight: 5% in 15 min counted, the 10h silent gap
        // adds neither time nor percent.
        let samples = vec![
            s(0, 50, true, true),
            s(900, 55, true, true),
            s(900 + 36000, 100, true, true),
            s(900 + 36000 + 1, 100, false, true),
        ];
        let (_, chg) = compute_spans(&samples);
        assert_eq!(chg.len(), 1);
        assert_eq!(chg[0].active_secs, 900);
        assert_eq!(chg[0].moved_pct, 5);
    }

    #[test]
    fn weighting_recent_full_extended_half_old_ignored() {
        let span = |end: i64, active: i64, moved: i32| Span {
            start_ts: end - active,
            end_ts: end,
            active_secs: active,
            level_start: 100,
            level_end: (100 - moved) as u8,
            moved_pct: moved,
        };
        // 10 recent cycles: 1h per 10% (rate 0.1 h/%); 2 older ones are much
        // worse (5h per 5%) and must only count at half weight.
        let mut spans: Vec<Span> = (0..10).map(|i| span(1000 + i, 3600, 10)).collect();
        spans.push(span(900, 5 * 3600, 5));
        spans.push(span(800, 5 * 3600, 5));
        // w_hours = 10*1 + 0.5*(5+5) = 15; w_pct = 10*1 + 0.5*(5+5) = 105.
        let rate = weighted_hours_per_pct(&spans).unwrap();
        assert!((rate - 15.0 / 105.0).abs() < 1e-9);

        // Beyond EXTENDED_LIMIT the oldest spans drop out entirely.
        let mut many: Vec<Span> = (0..10).map(|i| span(2000 + i, 3600, 10)).collect();
        many.extend((0..95).map(|i| span(1000 + i, 3600, 10)));
        many.push(span(100, 10 * 3600, 10)); // oldest, must be ignored
        // 100 counted spans * 1h / (100 * 10%) = 0.1 h/%.
        let rate = weighted_hours_per_pct(&many).unwrap();
        assert!((rate - 0.1).abs() < 1e-9);

        // Degenerate spans (tiny move / too short) never contribute.
        let junk = vec![
            span(500, 30, 1),  // too short
            span(600, 120, 2), // move below floor
            span(700, 6 * 3600, 10),
        ];
        let rate = weighted_hours_per_pct(&junk).unwrap();
        assert!((rate - 0.6).abs() < 1e-9);
        assert!(weighted_hours_per_pct(&[span(500, 30, 1)]).is_none());
    }

    #[test]
    fn predict_cycle_based_and_charge() {
        // 2.5h active for 50% drain → 0.05 h/% → 50% left = 2.5h.
        let mut samples: Vec<Sample> = vec![];
        for i in 0..=10i64 {
            samples.push(s(i * 900, (100 - i * 5) as u8, false, true));
        }
        samples.push(s(10 * 900 + 1, 50, true, true));
        samples.push(s(10 * 900 + 901, 100, true, true));
        samples.push(s(10 * 900 + 902, 100, false, true));
        let e = predict(&samples, 50, false).unwrap();
        assert!((e.secs - 9000).abs() <= 60);
        assert!(!e.charging);
        // Charged back 50→100 in 900s → 18s/% → from 80%: 360s to full.
        let e = predict(&samples, 80, true).unwrap();
        assert!((e.secs - 360).abs() <= 60);
        assert!(e.charging);
    }

    #[test]
    fn predict_falls_back_to_instant_rate() {
        // Only 30 min of history: no qualifying cycle, instant rate kicks in.
        let samples = vec![
            s(0, 80, false, true),
            s(600, 79, false, true),
            s(1200, 78, false, true),
            s(1800, 78, false, true),
        ];
        // 1200s active for 2% → 1/6 h/% → 78% left = 46800s.
        let e = predict(&samples, 78, false).unwrap();
        assert!(!e.charging);
        assert!((e.secs - 46800).abs() <= 60);
        // Charging: 20% gained in 30 min → 0.025 h/% → 90%→full = 900s.
        let chg = vec![
            s(0, 70, true, true),
            s(600, 76, true, true),
            s(1200, 88, true, true),
            s(1800, 90, true, true),
        ];
        let e = predict(&chg, 90, true).unwrap();
        assert!(e.charging);
        assert!((e.secs - 900).abs() <= 60);
        // Disconnected device: no prediction at all.
        let off = vec![
            s(0, 80, false, true),
            s(600, 79, false, true),
            s(1200, 78, false, false),
        ];
        assert!(predict(&off, 78, false).is_none());
    }

    #[test]
    fn swap_jump_predictions_stay_sane() {
        // Two swap-separated cycles with different rates: both are within
        // the newest-10 window and blend at full weight.
        let mut samples: Vec<Sample> = vec![];
        // Cycle 1: 30→20 over 5×900s (drop 10, active 4500s).
        for i in 0..=5i64 {
            samples.push(s(i * 900, (30 - i * 2) as u8, false, true));
        }
        // Swap: 20 → 80 (jump of 60 closes the cycle).
        samples.push(s(5 * 900 + 1, 80, false, true));
        // Cycle 2 (newest, open): 80→60 over 5×900s (drop 20, active 4500s).
        for i in 1..=5i64 {
            samples.push(s(5 * 900 + 1 + i * 900, (80 - i * 4) as u8, false, true));
        }
        let (dis, _) = compute_spans(&samples);
        assert_eq!(dis.len(), 2);
        assert_eq!(dis[0].moved_pct, 10);
        assert_eq!(dis[1].moved_pct, 20);
        // Weighted rate = (4501s + 4500s) / 30% ≈ 0.0833 h/% (the 1s swap
        // tick lands in cycle 1).
        let rate = weighted_hours_per_pct(&dis).unwrap();
        assert!((rate - 2.5 / 30.0).abs() < 1e-3);
    }
}
