// The live half of the identity heal: a fallback identity whose serial twin is
// already recorded must be archived even when the counterpart does not answer
// in that round — the store entry leaves the roster AND its battery.db row +
// samples are folded into the serial identity (HidWatcher.Commit aliases →
// HistoryService.MergeAlias). Measured 2026-11-10: the runtime fold logged
// "folding rooted fallback BLE:CF4FCB85ADF3 -> SI2..." while the ghost's
// 169-sample row stayed in battery.db, then keep_recorded_devices seeded it
// back into the tray/hover roster on every start.
//
// These tests need a real battery.db (APPDATA), so they run in the serialized
// collection that also owns the other APPDATA-overriding tests.

using Microsoft.Data.Sqlite;
using RazerTaskbar.Core;
using Xunit;

namespace RazerTaskbar.Tests;

[Collection("I18nSequential")]
public sealed class AliasHistoryLiveTests
{
    private const string Serial = "SI2522F18701637";
    private const string BleMac = "BLE:CF4FCB85ADF3";

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

    private static long Count(string db, string table, string handle)
    {
        using var conn = new SqliteConnection($"Data Source={db}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE handle = $h";
        cmd.Parameters.AddWithValue("$h", handle);
        return (long)cmd.ExecuteScalar()!;
    }

    private static string NewScratch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "razer-taskbar-alias-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static HidDeviceReading BleFallback(string name) => new(
        0x02CE, "", BleMac, 245, 10, false, name, DeviceKind.Keyboard,
        Transport: BatteryTransport.Ble);

    private static HidDeviceReading JoroOnDongle() => new(
        0x00B8, "", Serial, 219, 86, false, "Razer Joro", DeviceKind.Keyboard,
        Transport: BatteryTransport.Receiver);

    [Fact]
    public void CommitFoldsRecordedFallbackWhenItsOwnReadingStops()
    {
        var oldAppData = Environment.GetEnvironmentVariable("APPDATA");
        var scratch = NewScratch();
        Environment.SetEnvironmentVariable("APPDATA", scratch);
        try
        {
            HistoryService.Init();
            var store = new DeviceStore();
            var watcher = new HidWatcher();

            // Round 1: the device answers over Bluetooth before the identity
            // bridge knows its serial — the MAC fallback takes root and history
            // records it (this is how the ghost row was born).
            watcher.Commit(store, new List<HidDeviceReading> { BleFallback("Joro") }, shown: "");
            HistoryService.Record(store, recordEnabled: true);
            Assert.True(store.Snapshot().ContainsKey(BleMac));
            Assert.Equal(1L, Count(ConfigService.DbPath, "devices", BleMac));

            // Round 2: only the 2.4G dongle answers (real serial, canonical
            // name, dongle pid 0x00B8 — so the pid-pinned fold has no evidence
            // to work with); history now knows the serial side too.
            watcher.Commit(store, new List<HidDeviceReading> { JoroOnDongle() }, shown: "");
            HistoryService.Record(store, recordEnabled: true);

            // Round 3: the recorded fallback folds on the history pairing — no
            // same-round counterpart and no pid evidence required.
            watcher.Commit(store, new List<HidDeviceReading> { JoroOnDongle() }, shown: "");
            var snap = store.Snapshot();
            Assert.True(snap.ContainsKey(Serial));
            Assert.False(snap.ContainsKey(BleMac)); // out of the roster

            // …and out of battery.db, with its sample re-pointed, not dropped.
            HistoryService.Record(store, recordEnabled: true);
            Assert.Equal(0L, Count(ConfigService.DbPath, "devices", BleMac));
            Assert.Equal(0L, Count(ConfigService.DbPath, "samples", BleMac));
            Assert.True(Count(ConfigService.DbPath, "samples", Serial) >= 1);
            Assert.Equal(1L, Count(ConfigService.DbPath, "devices", Serial));
        }
        finally
        {
            HistoryService.Close();
            Environment.SetEnvironmentVariable("APPDATA", oldAppData);
            try { Directory.Delete(scratch, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void CommitMergesHistoryWhenTheBridgeFoldsAStaleFallbackEntry()
    {
        var oldAppData = Environment.GetEnvironmentVariable("APPDATA");
        var scratch = NewScratch();
        Environment.SetEnvironmentVariable("APPDATA", scratch);
        try
        {
            HistoryService.Init();
            var store = new DeviceStore();
            var watcher = new HidWatcher();

            // Round 1: rooted under the bare vendor name, recorded.
            watcher.Commit(store, new List<HidDeviceReading> { BleFallback("Joro") }, shown: "");
            HistoryService.Record(store, recordEnabled: true);
            Assert.Equal(1L, Count(ConfigService.DbPath, "devices", BleMac));

            // The log source lists the same device under its serial.
            store.Mutate(m => m[Serial] = new RazerDevice(
                "Razer Joro", Serial, 86, false, BatterySaver: false, IsConnected: true,
                IsSelected: true, Kind: DeviceKind.Keyboard, Transport: BatteryTransport.Receiver));

            // Round 2: the identity bridge learns the canonical name but not
            // the serial — ResolveIdentity pairs by exact name and drops the
            // stale entry. The entry leaving the store used to leave its
            // history behind.
            watcher.Commit(store, new List<HidDeviceReading> { BleFallback("Razer Joro") }, shown: "");

            var snap = store.Snapshot();
            Assert.True(snap.ContainsKey(Serial));
            Assert.False(snap.ContainsKey(BleMac));
            Assert.Equal(0L, Count(ConfigService.DbPath, "devices", BleMac));
            Assert.Equal(0L, Count(ConfigService.DbPath, "samples", BleMac));
            Assert.True(Count(ConfigService.DbPath, "samples", Serial) >= 1);
        }
        finally
        {
            HistoryService.Close();
            Environment.SetEnvironmentVariable("APPDATA", oldAppData);
            try { Directory.Delete(scratch, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ResolveIdentityFoldsIntoRecordedTwinAcrossTheNamingGap()
    {
        var name = "Joro";
        var reading = new HidDeviceReading(0x02CE, "", BleMac, 245, 96, false, name,
            DeviceKind.Keyboard, Transport: BatteryTransport.Ble);
        var map = new Dictionary<string, RazerDevice>
        {
            [Serial] = new RazerDevice("Razer Joro", Serial, 86, false, BatterySaver: false,
                IsConnected: true, IsSelected: true, Kind: DeviceKind.Keyboard),
        };

        // Without recorded history the bare name does not match the canonical
        // one: the fallback must stand alone (Synapse may simply not be there).
        var (handle, stale) = HidWatcher.ResolveIdentity(map, BleMac, reading, new HashSet<string>());
        Assert.Equal(BleMac, handle);
        Assert.Null(stale);

        // With the history pairing the two handles are known to be one device.
        var twins = new Dictionary<string, string> { [BleMac] = Serial };
        var (folded, dead) = HidWatcher.ResolveIdentity(map, BleMac, reading, new HashSet<string>(), twins);
        Assert.Equal(Serial, folded);
        Assert.Equal(BleMac, dead);

        // A recorded twin that is not in the store (offline) is no fold — the
        // live reading still needs a row to write into.
        var empty = new Dictionary<string, RazerDevice>();
        var (alone, none) = HidWatcher.ResolveIdentity(empty, BleMac, reading, new HashSet<string>(), twins);
        Assert.Equal(BleMac, alone);
        Assert.Null(none);

        // A serial identity already claimed this round (the log source got
        // there first) must not be folded into twice.
        var claimed = HidWatcher.ResolveIdentity(map, BleMac, reading, new HashSet<string> { Serial }, twins);
        Assert.Equal(BleMac, claimed.Handle);
        Assert.Null(claimed.Stale);
    }
}
