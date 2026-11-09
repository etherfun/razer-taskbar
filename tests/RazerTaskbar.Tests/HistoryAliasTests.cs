// Startup heal for identity pollution: a device that rooted under the
// HID:{pid}/BLE:{mac} fallback (serial query unanswered at boot) leaves a
// second devices row + sample series next to its real-serial row. The init
// alias merge folds them back together.
//
// Measured case (2026-11-10, live battery.db): the Joro's BLE fallback row was
// recorded as "Joro" (vendor channel's bare name) while its 2.4G/serial row is
// "Razer Joro" (Synapse's canonical name). The heal only accepted EXACT name
// equality, so the 169-sample ghost survived every restart — and with
// keep_recorded_devices it was seeded straight back into the device roster.

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
        Exec(conn, $"INSERT INTO devices(handle, name, first_seen, last_seen) VALUES('{Serial}', '{Name}', 100, 200)");
        Exec(conn, $"INSERT INTO devices(handle, name, first_seen, last_seen) VALUES('{Alias}', '{Name}', 50, 60)");
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
    public void MergeAliasReportsWhatMovedAndWhatTied()
    {
        using var conn = OpenSeededDb();
        var (moved, tied) = HistoryService.MergeAlias(conn, Alias, Serial);
        Assert.Equal(2, moved); // both alias samples land under the serial
        Assert.Equal(0, tied);

        // A second for which the canonical row already holds a reading is a
        // timestamp tie, not data loss: the serial sample wins, the count of
        // re-pointed samples says so.
        using var tied_ = OpenSeededDb();
        Exec(tied_, $"INSERT INTO samples VALUES('{Serial}', 50, 61, 0, 1)");
        var (moved2, tied2) = HistoryService.MergeAlias(tied_, Alias, Serial);
        Assert.Equal(1, moved2);
        Assert.Equal(1, tied2);
        Assert.Equal(61, Scalar(tied_, "SELECT level FROM samples WHERE handle = '" + Serial + "' AND ts = 50"));
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

        // Two name-matching serial rows could be two units of one model: ambiguous.
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

    [Fact]
    public void AliasPairsFoldsBareNameIntoCanonicalTwin()
    {
        // The measured ghost: the vendor BLE channel answers the bare product
        // name, Synapse logs the canonical one. Same device, two identities —
        // canonical-name equality (one leading "Razer" dropped) pairs them.
        var names = new Dictionary<string, string>
        {
            ["SI2522F18701637"] = "Razer Joro",
            ["BLE:CF4FCB85ADF3"] = "Joro",
        };
        var pair = Assert.Single(HistoryService.AliasPairs(names));
        Assert.Equal("BLE:CF4FCB85ADF3", pair.Src);
        Assert.Equal("SI2522F18701637", pair.Dst);

        // Canonicalisation also absorbs case and stray whitespace.
        var messy = new Dictionary<string, string>
        {
            ["SI2522F18701637"] = "  RAZER   Joro ",
            ["BLE:CF4FCB85ADF3"] = "joro",
        };
        Assert.Single(HistoryService.AliasPairs(messy));
    }

    [Fact]
    public void AliasPairsNeverFoldsNestedButDifferentModels()
    {
        // No pid evidence on this path, so the pairing rule must not use
        // substring containment: "DeathAdder V2" nests inside "DeathAdder V2
        // Pro" but is a DIFFERENT model — folding would pour one unit's
        // history into another. The safe failure mode is "no pair".
        var nested = new Dictionary<string, string>
        {
            ["MP09A12E31000D44"] = "Razer DeathAdder V2 Pro",
            ["HID:0088"] = "DeathAdder V2",
        };
        Assert.Empty(HistoryService.AliasPairs(nested));

        // …and the reverse direction (canonical twin is the shorter name).
        var reverse = new Dictionary<string, string>
        {
            ["MP09A12E31000D44"] = "Razer DeathAdder V2",
            ["HID:0088"] = "DeathAdder V2 Pro",
        };
        Assert.Empty(HistoryService.AliasPairs(reverse));
    }

    [Fact]
    public void AliasPairsKeepsTwoUnitsOfOneModelApart()
    {
        // Two units of one model both resolved a real serial, so the fallback's
        // canonical name matches TWO serial rows: it must stay alone — folding
        // it into either would pour one device's history into the other.
        var names = new Dictionary<string, string>
        {
            ["SI2522F18701637"] = "Razer Joro",
            ["632516H31000044"] = "Joro",
            ["BLE:CF4FCB85ADF3"] = "Joro",
        };
        Assert.Empty(HistoryService.AliasPairs(names));
    }

    [Fact]
    public void CanonicalDeviceNameRule()
    {
        // trim + internal whitespace collapse, case preserved (caller compares
        // OrdinalIgnoreCase), exactly ONE leading vendor token dropped.
        Assert.Equal("Joro", HistoryService.CanonicalDeviceName("  Razer   Joro "));
        Assert.Equal("Viper V3 HyperSpeed", HistoryService.CanonicalDeviceName("Razer Viper V3 HyperSpeed"));
        Assert.Equal("Razer Joro", HistoryService.CanonicalDeviceName("Razer Razer Joro")); // only one token
        Assert.Equal("Razer", HistoryService.CanonicalDeviceName("Razer")); // nothing left to drop
        Assert.Equal("", HistoryService.CanonicalDeviceName(""));
        Assert.Equal("", HistoryService.CanonicalDeviceName("   "));
        Assert.Equal("", HistoryService.CanonicalDeviceName(null));
        // A model word that merely starts with "Razer" is not the brand token.
        Assert.Equal("RazerEdge Pro", HistoryService.CanonicalDeviceName("RazerEdge Pro"));
    }

    [Fact]
    public void AliasPairsIgnoresEmptyNames()
    {
        var names = new Dictionary<string, string>
        {
            ["SI2522F18701637"] = "",
            ["BLE:CF4FCB85ADF3"] = "",
        };
        Assert.Empty(HistoryService.AliasPairs(names));

        // A fallback whose name canonicalises to nothing cannot pair either.
        var blank = new Dictionary<string, string>
        {
            ["SI2522F18701637"] = "Razer Joro",
            ["BLE:CF4FCB85ADF3"] = "   ",
        };
        Assert.Empty(HistoryService.AliasPairs(blank));
    }

    [Fact]
    public void StartupHealFoldsBareNamedGhostBeforeKeepRecordedSeeding()
    {
        // End to end on a real battery.db file: the heal runs inside
        // HistoryService.Init, i.e. before AppState seeds keep_recorded_devices
        // from HistoryService.KnownDevices() — so the folded ghost can never
        // re-enter the tray/hover roster as a recovery row.
        var oldAppData = Environment.GetEnvironmentVariable("APPDATA");
        var scratch = Path.Combine(Path.GetTempPath(), "razer-taskbar-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("APPDATA", scratch);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigService.DbPath)!);
            using (var seed = new SqliteConnection($"Data Source={ConfigService.DbPath}"))
            {
                seed.Open();
                HistoryService.EnsureTables(seed);
                HistoryService.UpsertDevice(seed, "SI2522F18701637", "Razer Joro", 100, BatteryTransport.Receiver);
                HistoryService.UpsertDevice(seed, "BLE:CF4FCB85ADF3", "Joro", 50, BatteryTransport.Ble);
                Exec(seed, "INSERT INTO samples VALUES('BLE:CF4FCB85ADF3', 50, 10, 0, 0)");
                Exec(seed, "INSERT INTO samples VALUES('BLE:CF4FCB85ADF3', 55, 10, 0, 0)");
                Exec(seed, "INSERT INTO samples VALUES('SI2522F18701637', 100, 86, 0, 1)");
            }

            HistoryService.Init();
            Assert.True(HistoryService.Ready());

            // The ghost is gone from the in-memory roster (history page picker)
            // and from what keep_recorded_devices would seed.
            Assert.Equal("SI2522F18701637", Assert.Single(HistoryService.ListDevices()).Handle);
            var known = HistoryService.KnownDevices();
            Assert.Equal("SI2522F18701637", Assert.Single(known).Handle);

            using var check = new SqliteConnection($"Data Source={ConfigService.DbPath}");
            check.Open();
            Assert.Equal(0L, Scalar(check, "SELECT COUNT(*) FROM devices WHERE handle = 'BLE:CF4FCB85ADF3'"));
            Assert.Equal(0L, Scalar(check, "SELECT COUNT(*) FROM samples WHERE handle = 'BLE:CF4FCB85ADF3'"));
            Assert.Equal(3L, Scalar(check, "SELECT COUNT(*) FROM samples WHERE handle = 'SI2522F18701637'"));
            // Timestamps are preserved, not rewritten.
            Assert.Equal(2L, Scalar(check,
                "SELECT COUNT(*) FROM samples WHERE handle = 'SI2522F18701637' AND ts IN (50, 55)"));
        }
        finally
        {
            HistoryService.Close();
            Environment.SetEnvironmentVariable("APPDATA", oldAppData);
            try { Directory.Delete(scratch, true); } catch (IOException) { }
        }
    }
}
