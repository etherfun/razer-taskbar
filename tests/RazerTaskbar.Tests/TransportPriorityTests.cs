// Cross-source read priority: USB HID (cable == 2.4G receiver) > Bluetooth
// LE > Synapse log. One link wins per device and is recorded on the device —
// and persisted with its row — so a device that answers on two links at once
// (cable/BT handover, or a dongle slot still answering while the device has
// moved to Bluetooth) keeps the better link's reading whatever order the two
// readings arrive in. A round that comes back short of the previous one is a
// failed acquisition and re-runs the chain once.

using Microsoft.Data.Sqlite;
using RazerTaskbar.Core;
using Xunit;

namespace RazerTaskbar.Tests;

public sealed class TransportPriorityTests
{
    private const string Serial = "632516H31000044";
    private const string Name = "Razer Viper V3 HyperSpeed";

    private static HidDeviceReading Reading(BatteryTransport transport, int level, bool? charging = false)
        => new(ProductId: 0x00B8, ProductName: Name, Serial: Serial,
            LevelRaw: (byte)level, LevelPercent: level, IsCharging: charging,
            Transport: transport);

    /// <summary>Push one poll round's readings through the real commit path
    /// and hand back the resulting store entry for the device.</summary>
    private static RazerDevice? Committed(params HidDeviceReading[] readings)
    {
        var store = new DeviceStore();
        new HidWatcher().Commit(store, readings.ToList(), "");
        return store.Snapshot().GetValueOrDefault(Serial);
    }

    // — tiering —

    [Fact]
    public void CableAndReceiverShareTheTopTier()
    {
        Assert.Equal(0, BatteryTransports.Priority(BatteryTransport.Wired));
        Assert.Equal(0, BatteryTransports.Priority(BatteryTransport.Receiver));
        // Same tier: neither ever displaces the other, only BLE and the log.
        Assert.False(BatteryTransports.Outranks(BatteryTransport.Wired, BatteryTransport.Receiver));
        Assert.False(BatteryTransports.Outranks(BatteryTransport.Receiver, BatteryTransport.Wired));
    }

    [Fact]
    public void UsbOutranksBleOutranksTheLog()
    {
        Assert.True(BatteryTransports.Outranks(BatteryTransport.Wired, BatteryTransport.Ble));
        Assert.True(BatteryTransports.Outranks(BatteryTransport.Receiver, BatteryTransport.Ble));
        Assert.True(BatteryTransports.Outranks(BatteryTransport.Ble, BatteryTransport.Log));
        Assert.False(BatteryTransports.Outranks(BatteryTransport.Ble, BatteryTransport.Receiver));
        Assert.False(BatteryTransports.Outranks(BatteryTransport.Log, BatteryTransport.Ble));
    }

    [Fact]
    public void UsbPidSplitsCableFromReceiver()
    {
        // 0x02CD is the wired Joro — the pid enumerates the device itself;
        // 0x00B8 is the Viper V3 HyperSpeed's 2.4G receiver. Both are the USB
        // tier, but the recorded link differs.
        Assert.Equal(BatteryTransport.Wired, RazerPidTable.UsbTransport(0x02CD));
        Assert.Equal(BatteryTransport.Receiver, RazerPidTable.UsbTransport(0x00B8));
    }

    // — one link wins per device, inside the round —

    [Fact]
    public void UsbReadingWinsWhateverTheArrivalOrder()
    {
        // BLE answered first (the dongle slot lost the device, the GATT read
        // came back): the USB reading still takes the entry over.
        var bleFirst = Committed(Reading(BatteryTransport.Ble, 72, null), Reading(BatteryTransport.Receiver, 61));
        Assert.NotNull(bleFirst);
        Assert.Equal(61, bleFirst!.BatteryPercentage);
        Assert.Equal(BatteryTransport.Receiver, bleFirst.Transport);

        // USB first: the later BLE reading is dropped, values and source alike.
        var usbFirst = Committed(Reading(BatteryTransport.Receiver, 61), Reading(BatteryTransport.Ble, 72, null));
        Assert.NotNull(usbFirst);
        Assert.Equal(61, usbFirst!.BatteryPercentage);
        Assert.Equal(BatteryTransport.Receiver, usbFirst.Transport);
    }

    [Fact]
    public void CableReadingIsRecordedAsWired()
    {
        var d = Committed(new HidDeviceReading(ProductId: 0x02CD, ProductName: "Razer Joro", Serial: Serial,
            LevelRaw: 80, LevelPercent: 80, IsCharging: false, Transport: BatteryTransport.Wired));
        Assert.NotNull(d);
        Assert.Equal(BatteryTransport.Wired, d!.Transport);
    }

    [Fact]
    public void BleOnlyDeviceRecordsBle()
    {
        var d = Committed(Reading(BatteryTransport.Ble, 72, null));
        Assert.NotNull(d);
        Assert.Equal(72, d!.BatteryPercentage);
        Assert.Equal(BatteryTransport.Ble, d.Transport);
    }

    // — a failed acquisition re-runs the chain once —

    [Fact]
    public void OnlyAShortRoundCountsAsAFailedAcquisition()
    {
        Assert.True(RazerWatcher.AcquisitionFailed(0, 1)); // the device is gone this round
        Assert.True(RazerWatcher.AcquisitionFailed(1, 2));
        // Steady state must not buy an extra poll every round: a device that
        // stayed absent makes the round short exactly once.
        Assert.False(RazerWatcher.AcquisitionFailed(1, 1));
        Assert.False(RazerWatcher.AcquisitionFailed(2, 1)); // recovered
        Assert.False(RazerWatcher.AcquisitionFailed(0, 0)); // nothing to lose yet
    }

    // — the winning link is persisted with the device row —

    [Fact]
    public void DeviceSourceRoundTripsThroughTheDatabase()
    {
        using var conn = OpenDb();
        HistoryService.UpsertDevice(conn, Serial, Name, 100, BatteryTransport.Receiver);
        Assert.Equal("Receiver", SourceOf(conn, Serial));

        // The same device answers over Bluetooth later: the stored source follows.
        HistoryService.UpsertDevice(conn, Serial, Name, 200, BatteryTransport.Ble);
        Assert.Equal("Ble", SourceOf(conn, Serial));
        Assert.Equal(BatteryTransport.Ble, HistoryService.ParseSavedSource(SourceOf(conn, Serial)));
    }

    [Fact]
    public void LegacyDatabaseGainsTheSourceColumn()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        // The schema an older build left behind (no source column).
        Exec(conn, "CREATE TABLE devices(handle TEXT PRIMARY KEY, name TEXT NOT NULL, " +
                   "first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL)");
        Exec(conn, $"INSERT INTO devices(handle, name, first_seen, last_seen) VALUES('{Serial}', '{Name}', 100, 200)");
        Assert.False(HasSource(conn));

        HistoryService.EnsureTables(conn);

        Assert.True(HasSource(conn));
        // The existing row survives and reads back as the log source.
        Assert.Equal(Name, NameOf(conn, Serial));
        Assert.Equal(BatteryTransport.Log, HistoryService.ParseSavedSource(SourceOf(conn, Serial)));
    }

    [Fact]
    public void UnknownStoredSourceReadsBackAsTheLog()
    {
        Assert.Equal(BatteryTransport.Log, HistoryService.ParseSavedSource(null));
        Assert.Equal(BatteryTransport.Log, HistoryService.ParseSavedSource(""));
        Assert.Equal(BatteryTransport.Log, HistoryService.ParseSavedSource("Carrier"));
        Assert.Equal(BatteryTransport.Ble, HistoryService.ParseSavedSource("Ble"));
        Assert.Equal(BatteryTransport.Wired, HistoryService.ParseSavedSource("Wired"));
    }

    // — helpers —

    private static SqliteConnection OpenDb()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        HistoryService.EnsureTables(conn);
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string? Scalar(SqliteConnection conn, string column, string handle)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM devices WHERE handle = $h";
        cmd.Parameters.AddWithValue("$h", handle);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : (string)value;
    }

    private static string? SourceOf(SqliteConnection conn, string handle) => Scalar(conn, "source", handle);

    private static string? NameOf(SqliteConnection conn, string handle) => Scalar(conn, "name", handle);

    private static bool HasSource(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('devices') WHERE name = 'source'";
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }
}
