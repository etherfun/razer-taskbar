// `keep_recorded_devices`: the battery.db read that seeds recorded-but-offline
// devices at startup, and the guard that keeps the recovery rows out of the
// history writer. The display-picker fallback itself is covered by
// DisplayModeTests.

using Microsoft.Data.Sqlite;
using RazerTaskbar.Core;
using Xunit;

namespace RazerTaskbar.Tests;

[Collection("I18nSequential")]
public sealed class KeepRecordedDevicesTests
{
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
    public void KnownDevicesReturnsLatestSamplePerDevice()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        HistoryService.EnsureTables(conn);
        HistoryService.UpsertDevice(conn, "m1", "Mouse A", 100, BatteryTransport.Receiver);
        HistoryService.UpsertDevice(conn, "k1", "Keyboard B", 100, BatteryTransport.Ble);
        // A device row without any sample (nothing to seed a level from) and
        // a pre-migration row whose source column is empty.
        Exec(conn, "INSERT INTO devices(handle, name, first_seen, last_seen) VALUES('x', 'Empty', 1, 1)");
        Exec(conn, "INSERT INTO samples VALUES('m1', 100, 50, 0, 1)");
        Exec(conn, "INSERT INTO samples VALUES('m1', 300, 47, 1, 0)"); // newest: offline, stale charging flag
        Exec(conn, "INSERT INTO samples VALUES('k1', 200, 88, 0, 1)");

        var known = HistoryService.KnownDevices(conn);

        // Deterministic name order (ordinal, case-insensitive).
        Assert.Equal(new[] { "Empty", "Keyboard B", "Mouse A" }, known.Select(k => k.Name));
        var mouse = known.Single(k => k.Handle == "m1");
        Assert.Equal(new Sample(300, 47, Charging: true, Connected: false), mouse.Latest);
        Assert.Equal(BatteryTransport.Receiver, mouse.Transport);
        Assert.Equal(BatteryTransport.Ble, known.Single(k => k.Handle == "k1").Transport);
        var empty = known.Single(k => k.Handle == "x");
        Assert.Equal(BatteryTransport.Log, empty.Transport); // empty source → log
        Assert.Null(empty.Latest);
    }

    [Fact]
    public void RecordDoesNotWriteSeededDevicesToHistory()
    {
        var oldAppData = Environment.GetEnvironmentVariable("APPDATA");
        var scratch = Path.Combine(Path.GetTempPath(), "razer-taskbar-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("APPDATA", scratch);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigService.DbPath)!);
            HistoryService.Init();
            Assert.True(HistoryService.Ready());

            var store = new DeviceStore();
            store.Mutate(m =>
            {
                m["live"] = new RazerDevice(
                    "Mouse", "live", 60, false, BatterySaver: false,
                    IsConnected: true, IsSelected: true, Kind: DeviceKind.Mouse);
                // A `keep_recorded_devices` recovery row: offline, no live
                // source has seen it this run.
                m["seed"] = new RazerDevice(
                    "Old Mouse", "seed", 33, false, BatterySaver: false,
                    IsConnected: false, IsSelected: true, Kind: DeviceKind.Mouse,
                    Transport: BatteryTransport.Receiver, IsSeeded: true);
            });

            HistoryService.Record(store, true);

            using var conn = new SqliteConnection($"Data Source={ConfigService.DbPath}");
            conn.Open();
            // The live device is recorded as usual; the recovery row must not
            // fabricate samples or a devices row (it is not an observation).
            Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM samples WHERE handle = 'live'"));
            Assert.Equal(0L, Scalar(conn, "SELECT COUNT(*) FROM samples WHERE handle = 'seed'"));
            Assert.Equal(0L, Scalar(conn, "SELECT COUNT(*) FROM devices WHERE handle = 'seed'"));
            Assert.True(store.Snapshot()["seed"].IsSeeded);
        }
        finally
        {
            HistoryService.Close();
            Environment.SetEnvironmentVariable("APPDATA", oldAppData);
            try { Directory.Delete(scratch, true); } catch (IOException) { }
        }
    }
}
