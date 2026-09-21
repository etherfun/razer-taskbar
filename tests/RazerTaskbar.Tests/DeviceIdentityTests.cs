// Tests for the HID↔log identity bridge (HidWatcher.ResolveIdentity): one
// physical device must not surface twice when the dongle's vendor serial
// query goes unanswered and the Synapse log already lists the same device
// under its real serial.

using RazerTaskbar.Core;
using Xunit;

namespace RazerTaskbar.Tests;

public sealed class DeviceIdentityTests
{
    private const string Serial = "MP09A12E31000D44";
    private const string Fallback = "HID:0088";

    private static readonly HidDeviceReading Reading =
        new(ProductId: 0x0088, ProductName: "Razer Viper V3 HyperSpeed", Serial: "",
            LevelRaw: 155, LevelPercent: 61, IsCharging: false);

    private static Dictionary<string, RazerDevice> Map(params RazerDevice[] devices)
        => devices.ToDictionary(d => d.Handle);

    [Fact]
    public void FallbackMergesIntoSameNameSerialTwin()
    {
        var map = Map(new RazerDevice("Razer Viper V3 HyperSpeed", Serial, 60, false,
            BatterySaver: false, IsConnected: true, IsSelected: true, Kind: DeviceKind.Mouse));
        var (handle, stale) = HidWatcher.ResolveIdentity(map, Fallback, Reading, new HashSet<string>());
        Assert.Equal(Serial, handle);
        Assert.Equal(Fallback, stale);
    }

    [Fact]
    public void FallbackStandsAloneWithoutSynapse()
    {
        // No log source (Synapse not running): the synthesized identity is
        // the only one — merging must not fire.
        var (handle, stale) = HidWatcher.ResolveIdentity(
            new Dictionary<string, RazerDevice>(), Fallback, Reading, new HashSet<string>());
        Assert.Equal(Fallback, handle);
        Assert.Null(stale);
    }

    [Fact]
    public void RealSerialHandleNeverMerges()
    {
        // A resolved serial is the shared identity across both sources; the
        // dictionary dedupes it without any bridge.
        var (handle, stale) = HidWatcher.ResolveIdentity(
            Map(), Serial, Reading, new HashSet<string>());
        Assert.Equal(Serial, handle);
        Assert.Null(stale);
    }

    [Fact]
    public void NameMismatchDoesNotMerge()
    {
        var map = Map(new RazerDevice("Razer Basilisk V3", Serial, 60, false,
            BatterySaver: false, IsConnected: true, IsSelected: true, Kind: DeviceKind.Mouse));
        var (handle, stale) = HidWatcher.ResolveIdentity(map, Fallback, Reading, new HashSet<string>());
        Assert.Equal(Fallback, handle);
        Assert.Null(stale);
    }

    [Fact]
    public void TwinClaimedThisRoundDoesNotMerge()
    {
        // The serial identity was already written by an earlier reading of
        // the same poll (the dongle answered its serial after all): the
        // fallback must not fold into it — that would be a second unit.
        var map = Map(new RazerDevice("Razer Viper V3 HyperSpeed", Serial, 60, false,
            BatterySaver: false, IsConnected: true, IsSelected: true, Kind: DeviceKind.Mouse));
        var (handle, stale) = HidWatcher.ResolveIdentity(
            map, Fallback, Reading, new HashSet<string> { Serial });
        Assert.Equal(Fallback, handle);
        Assert.Null(stale);
    }

    [Fact]
    public void NameMatchIsCaseInsensitive()
    {
        var map = Map(new RazerDevice("RAZER VIPER V3 HYPERSPEED", Serial, 60, false,
            BatterySaver: false, IsConnected: true, IsSelected: true, Kind: DeviceKind.Mouse));
        var (handle, _) = HidWatcher.ResolveIdentity(map, Fallback, Reading, new HashSet<string>());
        Assert.Equal(Serial, handle);
    }

    [Fact]
    public void BleFallbackMergesToo()
    {
        // The BLE MAC identity has the same fallback problem when the
        // Synapse heartbeat bridge is missing.
        var map = Map(new RazerDevice("Razer Joro", Serial, 96, false,
            BatterySaver: false, IsConnected: true, IsSelected: true, Kind: DeviceKind.Keyboard));
        var ble = new HidDeviceReading(0x02CE, "Razer Joro", "", 200, 96, true);
        var (handle, stale) = HidWatcher.ResolveIdentity(
            map, "BLE:001122334455", ble, new HashSet<string>());
        Assert.Equal(Serial, handle);
        Assert.Equal("BLE:001122334455", stale);
    }

    [Fact]
    public void AnotherFallbackIsNotATwin()
    {
        // Two pid-specific fallbacks must not merge into each other.
        var map = Map(new RazerDevice("Razer Viper V3 HyperSpeed", "HID:00B8", 60, false,
            BatterySaver: false, IsConnected: true, IsSelected: true, Kind: DeviceKind.Mouse));
        var (handle, stale) = HidWatcher.ResolveIdentity(map, Fallback, Reading, new HashSet<string>());
        Assert.Equal(Fallback, handle);
        Assert.Null(stale);
    }

    // — same-round pid dedup (DedupRound) —

    [Fact]
    public void SameRoundSerialWinsOverSamePidFallback()
    {
        // The mouse answered the battery query on two transaction ids of one
        // dongle; one slot's serial query failed. One physical device, one
        // entry — the serial identity wins, in either arrival order.
        var resolved = Reading with { Serial = Serial };
        Assert.Single(HidWatcher.DedupRound(new List<HidDeviceReading> { resolved, Reading }));
        Assert.Single(HidWatcher.DedupRound(new List<HidDeviceReading> { Reading, resolved }));
    }

    [Fact]
    public void FallbackOnlyRoundIsKept()
    {
        // Cold start with the radio still waking: the fallback is the only
        // identity available and the entry must exist.
        Assert.Single(HidWatcher.DedupRound(new List<HidDeviceReading> { Reading }));
    }

    [Fact]
    public void FallbackOfAnotherPidIsKept()
    {
        // A serial on pid 0x00C1 says nothing about pid 0x0088's device.
        var other = Reading with { ProductId = 0x00C1, Serial = Serial };
        var round = HidWatcher.DedupRound(new List<HidDeviceReading> { other, Reading });
        Assert.Equal(2, round.Count);
    }

    [Fact]
    public void ComboDongleKeyboardKeepsItsOwnFallback()
    {
        // The mouse resolved its serial; the keyboard slot's serial query
        // failed. Same pid, different devices: the keyboard keeps its own
        // `HID:{pid}:K` fallback. Dropping it (the old pid-only rule) flapped
        // the keyboard offline on every mouse wake.
        var mouse = Reading with { Serial = Serial };
        var keyboard = Reading with
        {
            NameOverride = "Razer Keyboard",
            KindOverride = DeviceKind.Keyboard,
            SubDeviceKey = "K",
        };
        var round = HidWatcher.DedupRound(new List<HidDeviceReading> { mouse, keyboard });
        Assert.Equal(2, round.Count);
        // The sibling's serial still retires the SAME slot's fallback.
        Assert.Single(HidWatcher.DedupRound(new List<HidDeviceReading> { keyboard with { Serial = Serial }, keyboard }));
    }

    [Fact]
    public void BleFallbackSurvivesSamePidSerial()
    {
        // BLE MAC fallbacks are per-device, not pid-derived: a resolved BLE
        // pid must not swallow another unit's MAC identity.
        var resolved = Reading with { Serial = Serial };
        var ble = Reading with { ProductId = 0x02CE, Serial = "BLE:001122334455" };
        var round = HidWatcher.DedupRound(new List<HidDeviceReading> { resolved, ble });
        Assert.Equal(2, round.Count);
    }

    // — combo-dongle sub-device keys: one pid, two sub-devices. Live
    // 2026-09-18: the Viper dongle's paired Joro keyboard rooted as the same
    // `HID:00B8` key as the mouse, and the mouse's serial resolution folded
    // the keyboard's 80% rows into the mouse's history as ±44% spikes —

    private const int ComboPid = 0x00B8; // Viper V3 HyperSpeed + Joro dongle

    private static RazerPidTable.DeviceSlot SlotOf(int pid, RazerPidTable.SlotRole role)
        => RazerPidTable.DeviceSlots(pid).First(s => s.Role == role);

    [Fact]
    public void SecondarySlotKeyIsSetForTheDonglesSecondSubDevice()
    {
        Assert.Null(RazerPidTable.SecondaryKey(ComboPid, SlotOf(ComboPid, RazerPidTable.SlotRole.Mouse)));
        Assert.Equal("K", RazerPidTable.SecondaryKey(ComboPid, SlotOf(ComboPid, RazerPidTable.SlotRole.Keyboard)));
        // Single-slot devices (the Joro in cable mode) keep the bare form.
        Assert.Null(RazerPidTable.SecondaryKey(0x02CD, SlotOf(0x02CD, RazerPidTable.SlotRole.Keyboard)));
    }

    [Fact]
    public void SecondarySlotFallbackCarriesItsKey()
    {
        var keyboard = new HidDeviceReading(ComboPid, "Razer Viper V3 HyperSpeed",
            Serial: "000000000000", LevelRaw: 200, LevelPercent: 80, IsCharging: false,
            NameOverride: "Razer Keyboard", KindOverride: DeviceKind.Keyboard,
            SubDeviceKey: "K");
        Assert.Equal("HID:00B8:K", HidWatcher.HandleFor(keyboard));
        Assert.Equal("HID:00B8", HidWatcher.HandleFor(keyboard with { SubDeviceKey = null }));
    }

    [Fact]
    public void SerialResolutionNeverRetiresTheSiblingSlotsFallback()
    {
        // The regression: the mouse answered with its serial while the
        // keyboard's fallback was still rooted. The retirement fold must take
        // only the mouse's own `HID:00B8` — the keyboard's rows stay in its
        // own series (they used to be re-pointed at the mouse and reach
        // battery.db as spikes).
        var store = new DeviceStore();
        var hid = new HidWatcher();
        var mouse = new HidDeviceReading(ComboPid, "Razer Viper V3 HyperSpeed", "", 90, 36, false);
        var keyboard = mouse with
        {
            LevelRaw = 200, LevelPercent = 80, NameOverride = "Razer Keyboard",
            KindOverride = DeviceKind.Keyboard, SubDeviceKey = "K",
        };
        hid.Commit(store, new List<HidDeviceReading> { mouse, keyboard }, "");
        var rooted = store.Snapshot();
        Assert.True(rooted.ContainsKey("HID:00B8"));
        Assert.True(rooted.ContainsKey("HID:00B8:K"));
        hid.Commit(store, new List<HidDeviceReading> { mouse with { Serial = Serial } }, "");
        var map = store.Snapshot();
        Assert.True(map.ContainsKey(Serial));
        Assert.False(map.ContainsKey("HID:00B8"));
        Assert.True(map.ContainsKey("HID:00B8:K"));
    }

    // — cross-round heal (Commit): the fallback rooted at cold start must be
    // retired the moment its device answers with the real serial —

    [Fact]
    public void SerialResolutionRetiresRootedFallbackEntry()
    {
        var store = new DeviceStore();
        var hid = new HidWatcher();
        hid.Commit(store, new List<HidDeviceReading> { Reading }, "");
        Assert.True(store.Snapshot().ContainsKey(Fallback));
        hid.Commit(store, new List<HidDeviceReading> { Reading with { Serial = Serial } }, "");
        var map = store.Snapshot();
        Assert.False(map.ContainsKey(Fallback));
        Assert.True(map.ContainsKey(Serial));
    }

    [Fact]
    public void SameRoundDuplicateNeverRootsFallback()
    {
        var store = new DeviceStore();
        var hid = new HidWatcher();
        hid.Commit(store, new List<HidDeviceReading> { Reading, Reading with { Serial = Serial } }, "");
        var map = store.Snapshot();
        Assert.False(map.ContainsKey(Fallback));
        Assert.True(map.ContainsKey(Serial));
    }
}
