// Port of src/watcher.rs #[cfg(test)] tests (V4 parsing rules).

using System.Text.Json;
using RazerTaskbar.Core;
using Xunit;

namespace RazerTaskbar.Tests;

public sealed class WatcherV4Tests
{
    /// <summary>Real shape from `systray_systrayv2*.log` (camelCase keys + nulls).</summary>
    private const string Sample = """[{"serialNumber":"632516H31000044","hasBattery":true,"deviceContainerId":"{9E502CF7-160A-51EA-8250-14BD19EB4A4A}","powerStatus":{"chargingStatus":"NoCharge_BatteryFull","level":82},"name":{"en":"Razer Viper V3 HyperSpeed"},"category":"MOUSE"},{"serialNumber":null,"hasBattery":true,"deviceContainerId":"{9E502CF7-160A-51EA-8250-14BD19EB4A4A}","powerStatus":null,"name":null,"category":null}]""";

    private static List<V4Device> Parse(string json)
        => JsonSerializer.Deserialize<List<V4Device>>(json)!;

    [Fact]
    public void V4CamelCaseFieldsDeserialize()
    {
        var devs = Parse(Sample);
        Assert.Equal(2, devs.Count);
        Assert.True(devs[0].HasBattery);
        Assert.Equal("632516H31000044", devs[0].SerialNumber);
        Assert.Equal(82, devs[0].PowerStatus.Level);
        Assert.Equal("Razer Viper V3 HyperSpeed", devs[0].Name.En);
        Assert.Equal("MOUSE", devs[0].Category);
    }

    [Fact]
    public void V4CategoryDrivesDeviceKind()
    {
        var devs = Parse(Sample);
        Assert.Equal(DeviceKind.Mouse, DeviceClassifier.FromCategoryAndName(devs[0].Category, devs[0].Name.En));
        // Null category + unknown name falls back to Other.
        Assert.Equal(DeviceKind.Other, DeviceClassifier.FromCategoryAndName(devs[1].Category, devs[1].Name.En));
    }

    [Fact]
    public void V4NullsFallBackToDefaults()
    {
        var devs = Parse(Sample);
        Assert.Equal("", devs[1].SerialNumber);
        Assert.Equal(0, devs[1].PowerStatus.Level);
        Assert.Equal("", devs[1].Name.En);
        // Null serial falls back to the container id (TS `??` parity).
        var handle = devs[1].SerialNumber.Length > 0 ? devs[1].SerialNumber : devs[1].DeviceContainerId;
        Assert.Equal("{9E502CF7-160A-51EA-8250-14BD19EB4A4A}", handle);
    }

    [Fact]
    public void V4ChargingFlagMatchesTsStrictEquality()
    {
        foreach (var (status, expected) in new[]
                 {
                     ("Charging", true),
                     ("NoCharge_BatteryFull", false),
                     ("off", false),
                     ("", false),
                 })
        {
            var json = """[{"serialNumber":"S","hasBattery":true,"deviceContainerId":"C","powerStatus":{"chargingStatus":"__STATUS__","level":50},"name":{"en":"N"}}]"""
                .Replace("__STATUS__", status);
            var devs = Parse(json);
            Assert.Equal(expected, devs[0].PowerStatus.ChargingStatus == "Charging");
        }
    }

    [Fact]
    public void LatestV4LogPicksHighestIndex()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"razer-taskbar-test-{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        foreach (var name in new[] { "systray_systrayv2.log", "systray_systrayv21.log", "systray_systrayv24.log" })
        {
            File.WriteAllText(Path.Combine(dir, name), "x");
        }
        var latest = RazerWatcher.LatestV4Log(dir);
        Assert.Equal("systray_systrayv24.log", Path.GetFileName(latest));
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>Real shapes from `systray_systrayv28.log`: a powered-off device STAYS
    /// in the snapshot with `chargingStatus: "off"` and a frozen level.</summary>
    private const string OffSnapshot = """[{"serialNumber":"632516H31000044","hasBattery":true,"deviceContainerId":"{C1}","powerStatus":{"chargingStatus":"NoCharge_BatteryFull","level":72},"name":{"en":"Razer Viper V3 HyperSpeed"},"category":"MOUSE"},{"serialNumber":"SI2522F18701637","hasBattery":true,"deviceContainerId":"{C1}","powerStatus":{"chargingStatus":"off","level":91},"name":{"en":"Razer Joro"},"category":"KEYBOARD"}]""";

    [Fact]
    public void V4PoweredOffDeviceInSnapshotIsDisconnected()
    {
        var devs = Parse(OffSnapshot);
        var ids = new HashSet<string>();
        var off = new HashSet<string>();
        foreach (var d in devs)
        {
            ids.Add(d.SerialNumber);
            if (d.PowerStatus.ChargingStatus == "off")
            {
                off.Add(d.SerialNumber);
            }
        }
        // Viper on, Joro powered off (but still listed).
        Assert.True(V4Rules.IsConnected(ids, off, "632516H31000044"));
        Assert.False(V4Rules.IsConnected(ids, off, "SI2522F18701637"));
        // A device that dropped out of the snapshot entirely stays offline.
        Assert.False(V4Rules.IsConnected(ids, off, "GONE"));
    }

    /// <summary>Transitional snapshot seen on wired (re)plug: the device is
    /// listed with a NULL powerStatus. Must not flash as connected at 0%.</summary>
    [Fact]
    public void V4NullPowerStatusIsDisconnected()
    {
        var json = """[{"serialNumber":"S","hasBattery":true,"deviceContainerId":"C","powerStatus":null,"name":{"en":"Razer Joro"},"category":"KEYBOARD"}]""";
        var devs = Parse(json);
        Assert.Equal(0, devs[0].PowerStatus.Level);
        Assert.Equal("", devs[0].PowerStatus.ChargingStatus);
        var ids = new HashSet<string>();
        var off = new HashSet<string>();
        var d = devs[0];
        ids.Add(d.SerialNumber);
        if (d.PowerStatus.ChargingStatus.Length == 0 || d.PowerStatus.ChargingStatus == "off")
        {
            off.Add(d.SerialNumber);
        }
        Assert.False(V4Rules.IsConnected(ids, off, "S"));
    }


    [Fact]
    public void ParseSerialNames_ExtractsNamePerSerial_LastSnapshotWins()
    {
        const string sample = """
            [2026/09/07 00:14:32.928] INFO  Service - connectingDeviceData: [{"serialNumber":"SI2522F18701637","name":{"en":"Razer Joro","zh-cn":"RAZER 乔罗金蛛"},"hasBattery":true}]
            [2026/09/07 06:53:32.708] INFO  Service - connectingDeviceData: [{"serialNumber":"632516H31000044","name":{"en":"Razer Viper V3 HyperSpeed"},"hasBattery":true},{"serialNumber":"SI2522F18701637","name":{"en":"Razer Joro Pro"},"hasBattery":true}]
            """;
        var names = RazerWatcher.ParseSerialNames(sample);
        Assert.Equal("Razer Joro Pro", names["SI2522F18701637"]); // last snapshot wins
        Assert.Equal("Razer Viper V3 HyperSpeed", names["632516H31000044"]);
    }

    // — Incremental batch application (ApplyV4Batch) —

    private const string BatchA60 = """[{"serialNumber":"A","hasBattery":true,"deviceContainerId":"CA","powerStatus":{"chargingStatus":"Discharging","level":60},"name":{"en":"Mouse A"},"category":"MOUSE"}]""";
    private const string BatchB40 = """[{"serialNumber":"B","hasBattery":true,"deviceContainerId":"CB","powerStatus":{"chargingStatus":"Discharging","level":40},"name":{"en":"Keyboard B"},"category":"KEYBOARD"}]""";
    private const string BatchA59C70 = """[{"serialNumber":"A","hasBattery":true,"deviceContainerId":"CA","powerStatus":{"chargingStatus":"Discharging","level":59},"name":{"en":"Mouse A"},"category":"MOUSE"},{"serialNumber":"C","hasBattery":true,"deviceContainerId":"CC","powerStatus":{"chargingStatus":"Charging","level":70},"name":{"en":"Headset C"},"category":"HEADSET"}]""";

    [Fact]
    public void V4BatchAppliesInOrderAndRetiresHandlesMissingFromLatestSnapshot()
    {
        // The incremental pass replays each batch in order (last write per
        // handle wins within the batch), then the disconnect rule: a handle
        // the source owns that the latest snapshot no longer lists reads
        // offline — the whole-file replay's net effect.
        var store = new DeviceStore();
        var known = new HashSet<string>();
        var conn = new HashSet<string> { "A", "B" };
        RazerWatcher.ApplyV4Batch(store,
            new List<(string, string)> { ("t1", BatchA60), ("t2", BatchB40) },
            conn, new HashSet<string>(), "", known);

        var snap = store.Snapshot();
        Assert.Equal(2, snap.Count);
        Assert.True(snap["A"].IsConnected);
        Assert.Equal(60, snap["A"].BatteryPercentage);
        Assert.True(snap["B"].IsConnected);

        // Batch 2: B drops out of the latest snapshot, C appears. B reads
        // offline keeping its last-known level and name.
        RazerWatcher.ApplyV4Batch(store, new List<(string, string)> { ("t3", BatchA59C70) },
            new HashSet<string> { "A", "C" }, new HashSet<string>(), "A", known);

        snap = store.Snapshot();
        Assert.Equal(3, snap.Count);
        Assert.True(snap["A"].IsConnected);
        Assert.Equal(59, snap["A"].BatteryPercentage);
        Assert.True(snap["A"].IsSelected); // shown=A marks the selection
        Assert.False(snap["C"].IsSelected);
        Assert.False(snap["B"].IsConnected);
        Assert.Equal(40, snap["B"].BatteryPercentage);
        Assert.Equal("Keyboard B", snap["B"].Name);
        Assert.Equal(3, known.Count);
    }

    [Fact]
    public void V4BatchSkipsCorruptLinesAndRetiresNoSerialDuplicate()
    {
        var store = new DeviceStore();
        var known = new HashSet<string>();
        // The id set always mirrors the batch's last snapshot (the parse
        // path derives it from that snapshot).
        var conn = new HashSet<string> { "A" };
        // A corrupt line between good ones must not abort the batch.
        RazerWatcher.ApplyV4Batch(store,
            new List<(string, string)> { ("t1", BatchA60), ("t2", "{not json") },
            conn, new HashSet<string>(), "", known);
        Assert.True(store.Snapshot()["A"].IsConnected);

        // The literal NOSERIALNUMBER handle retires once the real serial
        // resolves under the same name (TS parity dedup).
        RazerWatcher.ApplyV4Batch(store, new List<(string, string)> { ("t3", BatchA60) },
            conn, new HashSet<string>(), "", known);
        store.Mutate(m => m["NOSERIALNUMBER"] = m["A"] with { Handle = "NOSERIALNUMBER" });
        RazerWatcher.ApplyV4Batch(store, new List<(string, string)> { ("t4", BatchA60) },
            conn, new HashSet<string>(), "", known);
        Assert.False(store.Snapshot().ContainsKey("NOSERIALNUMBER"));
    }

    // — incremental slice assembly (V4CompleteLines) —

    private static byte[] B(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    [Fact]
    public void CompleteLinesCarryTheTrailingFragment()
    {
        // The steady-state append: whole lines parse, the line caught mid-write
        // waits for its rest.
        var (text, pending) = RazerWatcher.V4CompleteLines(B("line1\n"), B("line2\nline3"), false);
        Assert.Equal("line1\nline2\n", text);
        Assert.Equal("line3", System.Text.Encoding.UTF8.GetString(pending));

        // Nothing terminated yet: everything stays pending.
        var (none, all) = RazerWatcher.V4CompleteLines(B("part"), B("ial"), false);
        Assert.Equal("", none);
        Assert.Equal("partial", System.Text.Encoding.UTF8.GetString(all));
    }

    [Fact]
    public void TailReadDropsTheLeadingFragment()
    {
        // A tail read starts mid-line. The fragment must not reach the parser:
        // a cut inside a JSON array can leave text that matches the snapshot
        // regex, and a bogus timestamp/handle would be replayed as a snapshot.
        var slice = B("SER\":\"X\"}]\n[09:00:00] connectingDeviceData: [{\"a\":1}]\n[09:01:00] connectingDeviceData: [{\"b\":2}]");
        var (text, pending) = RazerWatcher.V4CompleteLines(Array.Empty<byte>(), slice, startsMidLine: true);
        Assert.Equal("[09:00:00] connectingDeviceData: [{\"a\":1}]\n", text);
        Assert.Equal("[09:01:00] connectingDeviceData: [{\"b\":2}]",
            System.Text.Encoding.UTF8.GetString(pending));

        // The same slice read from a line boundary keeps its first line — the
        // incremental path must not drop anything.
        var (whole, _) = RazerWatcher.V4CompleteLines(Array.Empty<byte>(), slice, startsMidLine: false);
        Assert.StartsWith("SER\":\"X\"}]", whole);
    }

    [Fact]
    public void TailReadWithASingleLineKeepsItPending()
    {
        // The window landed inside one line: the fragment is dropped, the
        // partial line is carried, and nothing is parsed yet.
        var (text, pending) = RazerWatcher.V4CompleteLines(
            Array.Empty<byte>(), B("fragment\npartial"), startsMidLine: true);
        Assert.Equal("", text);
        Assert.Equal("partial", System.Text.Encoding.UTF8.GetString(pending));
    }

    [Fact]
    public void HarvestFallsBackToTheWholeFileOnlyForAMissingSerial()
    {
        // The tail is a fast path, not a bound: the serial→name mapping is a
        // historical fact, so a serial the newest snapshots no longer mention
        // (live: a keyboard asleep for an hour, its mapping 475 KB from the end
        // of a 4.0 MB file) must still send the harvest back over the whole
        // file — otherwise the slot silently falls back to "Razer Keyboard".
        var tail = new Dictionary<string, string> { ["SI2522F18701637"] = "Razer Joro" };
        Assert.False(RazerWatcher.NeedsWholeFile(tail, "SI2522F18701637"));
        Assert.True(RazerWatcher.NeedsWholeFile(tail, "632516H31000044"));
        // No serial asked for (or none known): the tail's answer stands.
        Assert.False(RazerWatcher.NeedsWholeFile(tail, null));
        Assert.False(RazerWatcher.NeedsWholeFile(tail, ""));
    }
}
