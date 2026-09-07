// Startup heal for identity pollution: a device that rooted under the
// HID:{pid}/BLE:{mac} fallback (serial query unanswered at boot) leaves a
// second devices row + sample series next to its real-serial row. The init
// alias merge folds them back together.

using Microsoft.Data.Sqlite;
using RazerTaskbar.Core;
using Xunit;

namespace RazerTaskbar.Tests;

public sealed class HistoryAliasTests
{
    private const string Serial = "632516H31000044";
    private const string Alias = "HID:00B8";
    private const string Name = "Razer Viper V3 HyperSpeed";

    private static SqliteConnection OpenSeededDb()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        HistoryService.EnsureTables(conn);
        Exec(conn, $"INSERT INTO devices VALUES('{Serial}', '{Name}', 100, 200)");
        Exec(conn, $"INSERT INTO devices VALUES('{Alias}', '{Name}', 50, 60)");
        // alias-rooted samples (cold start before the serial resolved)…
        Exec(conn, $"INSERT INTO samples VALUES('{Alias}', 50, 64, 0, 1)");
        Exec(conn, $"INSERT INTO samples VALUES('{Alias}', 55, 63, 0, 1)");
        // …plus the canonical series recorded after the resolution.
        Exec(conn, $"INSERT INTO samples VALUES('{Serial}', 100, 62, 0, 1)");
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void AliasMergeRepointsSamplesAndDropsAliasRow()
    {
        using var conn = OpenSeededDb();
        HistoryService.MergeAlias(conn, Alias, Serial);
        Assert.Equal(3, Scalar(conn, $"SELECT COUNT(*) FROM samples WHERE handle = '{Serial}'"));
        Assert.Equal(0, Scalar(conn, $"SELECT COUNT(*) FROM samples WHERE handle = '{Alias}'"));
        Assert.Equal(0, Scalar(conn, $"SELECT COUNT(*) FROM devices WHERE handle = '{Alias}'"));
        Assert.Equal(1, Scalar(conn, $"SELECT COUNT(*) FROM devices WHERE handle = '{Serial}'"));
    }

    [Fact]
    public void TimestampTieKeepsCanonicalSample()
    {
        using var conn = OpenSeededDb();
        Exec(conn, $"INSERT INTO samples VALUES('{Serial}', 50, 61, 0, 1)"); // same ts as an alias sample
        HistoryService.MergeAlias(conn, Alias, Serial);
        // The identity recorded under the serial wins the tie at ts=50; the
        // alias's other sample (ts=55) is still re-pointed: 100, 50, 55.
        Assert.Equal(3, Scalar(conn, $"SELECT COUNT(*) FROM samples WHERE handle = '{Serial}'"));
        Assert.Equal(61, Scalar(conn, "SELECT level FROM samples WHERE handle = '" + Serial + "' AND ts = 50"));
    }

    [Fact]
    public void AliasPairsRule()
    {
        var names = new Dictionary<string, string>
        {
            [Serial] = Name,
            [Alias] = Name,
            ["SI2522F18701637"] = "Razer Joro",
        };
        var pairs = HistoryService.AliasPairs(names);
        var pair = Assert.Single(pairs);
        Assert.Equal((Alias, Serial), (pair.Src, pair.Dst));

        // Two same-name serial rows could be two units of one model: ambiguous.
        var twoUnits = new Dictionary<string, string>
        {
            [Serial] = Name,
            ["MP09A12E31000D44"] = Name,
            [Alias] = Name,
        };
        Assert.Empty(HistoryService.AliasPairs(twoUnits));

        // A fallback with no same-name serial twin is the only record — keep.
        var solo = new Dictionary<string, string> { [Alias] = Name };
        Assert.Empty(HistoryService.AliasPairs(solo));
    }
}
