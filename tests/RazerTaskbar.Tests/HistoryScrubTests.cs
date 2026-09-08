// Startup heal for glitch rows that reached battery.db before the
// write-time SpikeFilter existed (or through its stale-anchor blind spot
// at reconnects): CommittedGlitchTs flags suspect jumps whose level fell
// back inside the grace window; the Init scrub deletes those rows from
// memory and the database.

using Microsoft.Data.Sqlite;
using RazerTaskbar.Core;
using Xunit;

namespace RazerTaskbar.Tests;

public sealed class HistoryScrubTests
{
    private static Sample S(long ts, int level, bool charging, bool connected)
        => new(ts, level, charging, connected);

    [Fact]
    public void ScrubsReconnectGlitchCommittedByStaleAnchor()
    {
        // Production rows from battery.db (2026-09-08 06:56, Viper
        // 632516H31000044): a disconnected heartbeat at 61%, the reconnect
        // glitch 100%, the true 65%, one more 100% oscillation, true 65%
        // again. Exactly the two 100% rows go; every true reading stays.
        var hist = new List<Sample>
        {
            S(0, 61, false, false),
            S(471, 100, false, true),
            S(473, 65, false, true),
            S(477, 100, false, true),
            S(478, 65, false, true),
            S(531, 65, false, true),
            S(1044, 63, false, true),
        };
        Assert.Equal(new long[] { 471, 477 }, HistoryService.CommittedGlitchTs(hist));
    }

    [Fact]
    public void ScrubsToleranceShapeCommittedByPreGuardBuilds()
    {
        // 85 → 100 → 82: only a 15-point jump, so pre-guard builds wrote
        // it outright; the rise rule flags it and the tolerance fall-back
        // confirms it.
        var hist = new List<Sample>
        {
            S(0, 85, false, true),
            S(5, 100, false, true),
            S(10, 82, false, true),
        };
        Assert.Equal(new long[] { 5 }, HistoryService.CommittedGlitchTs(hist));
    }

    [Fact]
    public void KeepsRealSwap()
    {
        // 20 → 90 with no fall-back inside the window is a battery swap.
        var hist = new List<Sample>
        {
            S(0, 20, false, true),
            S(10, 90, false, true),
            S(80, 89, false, true),
        };
        Assert.Empty(HistoryService.CommittedGlitchTs(hist));
    }

    [Fact]
    public void KeepsStableReconnect()
    {
        // Reconnect 8 minutes after a 61% heartbeat at the true 65%: a
        // 4-point rise is under the rise rule — nothing suspect.
        var hist = new List<Sample>
        {
            S(0, 61, false, false),
            S(471, 65, false, true),
            S(531, 65, false, true),
        };
        Assert.Empty(HistoryService.CommittedGlitchTs(hist));
    }

    [Fact]
    public void WindowExpiryKeepsSuspectJump()
    {
        // The fall-back arrives after the grace window: committed as real
        // at write time, kept at scrub time — the same 60s contract.
        var hist = new List<Sample>
        {
            S(0, 61, false, true),
            S(10, 100, false, true),
            S(80, 63, false, true),
        };
        Assert.Empty(HistoryService.CommittedGlitchTs(hist));
    }

    [Fact]
    public void DisconnectEchoConfirmsGlitch()
    {
        // Glitch, then the device vanished before returning: the
        // disconnect echo (pre-jump level, con=0) confirms via tolerance.
        var hist = new List<Sample>
        {
            S(0, 61, false, true),
            S(10, 100, false, true),
            S(15, 61, false, false),
        };
        Assert.Equal(new long[] { 10 }, HistoryService.CommittedGlitchTs(hist));
    }

    [Fact]
    public void ScrubDeletesRowsFromDatabase()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        HistoryService.EnsureTables(conn);
        Exec(conn, "INSERT INTO samples VALUES('dev', 0, 61, 0, 0)");
        Exec(conn, "INSERT INTO samples VALUES('dev', 471, 100, 0, 1)");
        Exec(conn, "INSERT INTO samples VALUES('dev', 473, 65, 0, 1)");
        Exec(conn, "INSERT INTO samples VALUES('dev', 531, 65, 0, 1)");
        var hist = new List<Sample>
        {
            S(0, 61, false, false),
            S(471, 100, false, true),
            S(473, 65, false, true),
            S(531, 65, false, true),
        };
        Assert.Equal(1, HistoryService.DeleteSamples(conn, "dev", HistoryService.CommittedGlitchTs(hist)));
        Assert.Equal(3, Count(conn));
        Assert.Equal(0, Count(conn, "ts = 471"));
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Count(SqliteConnection conn, string? where = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM samples{(where is null ? "" : $" WHERE {where}")}";
        return (long)cmd.ExecuteScalar()!;
    }
}
