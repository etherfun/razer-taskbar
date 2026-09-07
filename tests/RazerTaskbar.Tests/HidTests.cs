// Tests for the direct-HID battery protocol (RazerReport / RazerPidTable)
// and the HID reading → RazerDevice mapping. Pure functions only — no
// hardware, no P/Invoke.

using RazerTaskbar.Core;
using Xunit;

namespace RazerTaskbar.Tests;

public class HidTests
{
    private static byte[] MakeResponse(byte tx, byte commandId, byte status, byte arg1)
    {
        var buf = new byte[RazerReport.HidBufferSize];
        buf[1] = status; // payload status
        buf[2] = tx;
        buf[7] = RazerReport.CommandClassBattery;
        buf[8] = commandId;
        buf[10] = arg1; // arguments[1]
        buf[89] = RazerReport.CalculateCrc(buf);
        return buf;
    }

    [Fact]
    public void BatteryQuery_Layout()
    {
        var buf = RazerReport.BuildBatteryQuery(0x1F);
        Assert.Equal(RazerReport.HidBufferSize, buf.Length); // 91 = report id + 90
        Assert.Equal(0x00, buf[0]); // report id: none
        Assert.Equal(0x00, buf[1]); // status NEW_COMMAND
        Assert.Equal(0x1F, buf[2]); // transaction id
        Assert.Equal(0x00, buf[3]); // remaining packets (big-endian 0)
        Assert.Equal(0x00, buf[5]); // protocol type
        Assert.Equal(0x02, buf[6]); // data size
        Assert.Equal(0x07, buf[7]); // command class BATTERY
        Assert.Equal(0x80, buf[8]); // command id GET_BATTERY_LEVEL
        Assert.All(buf[9..88], b => Assert.Equal(0, b)); // arguments zeroed
        Assert.Equal(0x85, buf[89]); // 0x02 ^ 0x07 ^ 0x80
        Assert.Equal(0x00, buf[90]); // reserved
    }

    [Fact]
    public void ChargingQuery_CrcVector()
    {
        var buf = RazerReport.BuildChargingQuery(0x1F);
        Assert.Equal(0x84, buf[8]);
        Assert.Equal(0x81, buf[89]); // 0x02 ^ 0x07 ^ 0x84
        Assert.Equal(buf[89], RazerReport.CalculateCrc(buf));
    }

    [Fact]
    public void ParseResponse_Success_CarriesArgument1()
    {
        var response = MakeResponse(0x1F, 0x80, 0x02, arg1: 161);
        var kind = RazerReport.ParseResponse(response, 0x1F, 0x07, 0x80, out var arg1);
        Assert.Equal(RazerResponseKind.Success, kind);
        Assert.Equal((byte)161, arg1);
    }

    [Fact]
    public void ParseResponse_RetryableStatuses_ShortCircuitBeforeEcho()
    {
        // Busy/no-response answers may carry a stale header, so they are
        // classified before echo validation.
        Assert.Equal(RazerResponseKind.Busy, RazerReport.ParseResponse(MakeResponse(0xEE, 0xEE, 0x01, 0), 0x1F, 0x07, 0x80, out _));
        Assert.Equal(RazerResponseKind.NoResponse, RazerReport.ParseResponse(MakeResponse(0xEE, 0xEE, 0x04, 0), 0x1F, 0x07, 0x80, out _));
    }

    [Fact]
    public void ParseResponse_FailureAndUnsupported_AreFinal()
    {
        Assert.Equal(RazerResponseKind.NotSupported, RazerReport.ParseResponse(MakeResponse(0x1F, 0x84, 0x03, 0), 0x1F, 0x07, 0x84, out _));
        Assert.Equal(RazerResponseKind.NotSupported, RazerReport.ParseResponse(MakeResponse(0x1F, 0x84, 0x05, 0), 0x1F, 0x07, 0x84, out _));
    }

    [Fact]
    public void ParseResponse_RejectsStaleHeader()
    {
        var response = MakeResponse(0x1F, 0x84, 0x02, 0); // charging answer…
        // …but we asked for the battery level: echo mismatch.
        Assert.Equal(RazerResponseKind.BadEcho, RazerReport.ParseResponse(response, 0x1F, 0x07, 0x80, out _));
        // Wrong transaction id.
        Assert.Equal(RazerResponseKind.BadEcho, RazerReport.ParseResponse(response, 0x3F, 0x07, 0x84, out _));
    }

    [Fact]
    public void ParseResponse_RejectsCorruptedPayload()
    {
        var response = MakeResponse(0x1F, 0x80, 0x02, arg1: 161);
        response[10] ^= 0xFF; // corrupt arguments[1] behind the CRC's back
        Assert.Equal(RazerResponseKind.BadCrc, RazerReport.ParseResponse(response, 0x1F, 0x07, 0x80, out _));
    }

    [Fact]
    public void ParseResponse_RejectsShortBuffer()
    {
        Assert.Equal(RazerResponseKind.BadLength, RazerReport.ParseResponse(new byte[10], 0x1F, 0x07, 0x80, out _));
    }

    [Fact]
    public void LevelPercent_ScalingRules()
    {
        Assert.Equal(63, RazerReport.LevelPercent(63, BatteryScale.Direct100));
        Assert.Equal(100, RazerReport.LevelPercent(255, BatteryScale.Direct100)); // capped
        Assert.Equal(63, RazerReport.LevelPercent(161, BatteryScale.Scaled255)); // 161*100/255 = 63.1
        Assert.Equal(100, RazerReport.LevelPercent(255, BatteryScale.Scaled255));
        Assert.Equal(63, RazerReport.LevelPercent(63, BatteryScale.Auto)); // ≤100 → direct
        Assert.Equal(63, RazerReport.LevelPercent(161, BatteryScale.Auto)); // >100 → scaled
        Assert.Equal(78, RazerReport.LevelPercent(200, BatteryScale.Auto));
    }

    [Fact]
    public void PidTable_KnownPidsSkipProbing()
    {
        Assert.Equal(new byte[] { 0x1F }, RazerPidTable.TransactionIdCandidates(0x00B8)); // Viper V3 HyperSpeed
        Assert.Equal(new byte[] { 0x3F }, RazerPidTable.TransactionIdCandidates(0x007D)); // DeathAdder V2 Pro
        Assert.Equal(new byte[] { 0xFF }, RazerPidTable.TransactionIdCandidates(0x007B)); // Viper Ultimate
        Assert.Equal(new byte[] { 0x9F }, RazerPidTable.TransactionIdCandidates(0x025C)); // BW V3 Pro wireless
        Assert.Equal(RazerPidTable.FallbackTransactionIds, RazerPidTable.TransactionIdCandidates(0x1234)); // unknown → probe
        // Mice scale 0..255 (confirmed on a Viper V3 HyperSpeed: raw 161 = 63%).
        Assert.Equal(BatteryScale.Scaled255, RazerPidTable.ScaleFor(0x00B8));
        Assert.Equal(BatteryScale.Scaled255, RazerPidTable.ScaleFor(0x007B));
        Assert.Equal(BatteryScale.Auto, RazerPidTable.ScaleFor(0x1234));
    }

    [Fact]
    public void HandleFor_UsesSerial_WithPidFallback()
    {
        Assert.Equal("632516H31000044", HidWatcher.HandleFor(new HidDeviceReading(0x00B8, "Razer Viper V3 HyperSpeed", "632516H31000044", 63, 63, false)));
        Assert.Equal("HID:02CD", HidWatcher.HandleFor(new HidDeviceReading(0x02CD, "Razer Joro", "", 100, 100, null)));
        Assert.Equal("HID:02CD", HidWatcher.HandleFor(new HidDeviceReading(0x02CD, "Razer Joro", "  ", 100, 100, null)));
        // Dongles report an all-zero serial string; that is not an identity.
        Assert.Equal("HID:00B8", HidWatcher.HandleFor(new HidDeviceReading(0x00B8, "Razer Viper V3 HyperSpeed", "000000000000", 63, 63, false)));
    }

    [Fact]
    public void ToRazerDevice_MapsLikeLogEntries()
    {
        var reading = new HidDeviceReading(0x00B8, "Razer Viper V3 HyperSpeed", "632516H31000044", 63, 63, false);
        var device = HidWatcher.ToRazerDevice(reading, "632516H31000044", shownHandle: "");
        Assert.Equal("Razer Viper V3 HyperSpeed", device.Name);
        Assert.Equal("632516H31000044", device.Handle);
        Assert.Equal(63, device.BatteryPercentage);
        Assert.False(device.IsCharging);
        Assert.False(device.BatterySaver); // not available over HID
        Assert.True(device.IsConnected);
        Assert.True(device.IsSelected); // empty shown handle = all selected
        Assert.Equal(DeviceKind.Mouse, device.Kind);

        var joro = HidWatcher.ToRazerDevice(
            new HidDeviceReading(0x02CD, "Razer Joro", "SI2522F18701637", 100, 100, true),
            "SI2522F18701637", shownHandle: "632516H31000044");
        Assert.Equal(DeviceKind.Keyboard, joro.Kind);
        Assert.True(joro.IsCharging);
        Assert.False(joro.IsSelected); // another device is pinned
    }

    [Fact]
    public void ToRazerDevice_UnnamedProduct_FallsBackToPidName()
    {
        var device = HidWatcher.ToRazerDevice(
            new HidDeviceReading(0x02CD, "", "", 50, 50, null), "HID:02CD", shownHandle: "");
        Assert.Equal("Razer device 0x02CD", device.Name);
        Assert.Equal(DeviceKind.Other, device.Kind);
    }

    [Fact]
    public void SerialQuery_Layout()
    {
        var buf = RazerReport.BuildSerialQuery(0x1F);
        Assert.Equal(RazerReport.HidBufferSize, buf.Length);
        Assert.Equal(0x16, buf[6]); // data size: 22 argument bytes
        Assert.Equal(0x00, buf[7]); // command class INFO
        Assert.Equal(0x82, buf[8]); // command id GET_SERIAL
        Assert.Equal(0x94, buf[89]); // 0x16 ^ 0x00 ^ 0x82
    }

    [Fact]
    public void TryGetSerial_ExtractsPrintableArguments()
    {
        var response = MakeResponse(0x1F, 0x82, 0x02, 0);
        response[7] = 0x00; // class INFO (arguments are zero-initialized)
        "632516H31000044"u8.ToArray().CopyTo(response, 9);
        response[89] = RazerReport.CalculateCrc(response);
        Assert.True(RazerReport.TryGetSerial(response, 0x1F, out var serial));
        Assert.Equal("632516H31000044", serial);
    }

    [Fact]
    public void TryGetSerial_RejectsGarbageAndShortStrings()
    {
        var response = MakeResponse(0x1F, 0x82, 0x02, 0);
        response[7] = 0x00;
        response[9] = 0x01; // non-printable garbage (OpenRazer issue #2122)
        "UNKWN123"u8.ToArray().CopyTo(response, 10);
        response[89] = RazerReport.CalculateCrc(response);
        Assert.False(RazerReport.TryGetSerial(response, 0x1F, out _));

        var shortOne = MakeResponse(0x1F, 0x82, 0x02, 0);
        shortOne[7] = 0x00;
        "AB"u8.ToArray().CopyTo(shortOne, 9);
        shortOne[89] = RazerReport.CalculateCrc(shortOne);
        Assert.False(RazerReport.TryGetSerial(shortOne, 0x1F, out _));
    }


    [Fact]
    public void DeviceSlots_ComboDongleHostsMouseAndKeyboard()
    {
        var slots = RazerPidTable.DeviceSlots(0x00B8); // Viper V3 HS combo dongle
        Assert.Equal(2, slots.Count);
        Assert.Equal((byte)0x1F, slots[0].TransactionId);
        Assert.Equal(RazerPidTable.SlotRole.Mouse, slots[0].Role);
        Assert.Equal((byte)0x9F, slots[1].TransactionId);
        Assert.Equal(RazerPidTable.SlotRole.Keyboard, slots[1].Role);
        Assert.Equal(BatteryScale.Scaled255, slots[1].Scale); // Joro: raw 255 = 100%
    }

    [Fact]
    public void DeviceSlots_WiredJoroIsSingleKeyboardSlot()
    {
        // Cable mode: independent USB device, tx 0x1F despite being a
        // keyboard (confirmed by probe: serial + battery + charging answer).
        var slots = RazerPidTable.DeviceSlots(0x02CD);
        Assert.Single(slots);
        Assert.Equal((byte)0x1F, slots[0].TransactionId);
        Assert.Equal(RazerPidTable.SlotRole.Keyboard, slots[0].Role);
        Assert.Equal(BatteryScale.Scaled255, slots[0].Scale); // raw 255 = 100%
        Assert.Equal(new byte[] { 0x1F }, RazerPidTable.TransactionIdCandidates(0x02CD));
    }

    [Fact]
    public void HandleFor_JoroWiredMatchesWirelessSerial()
    {
        // Cable mode answers on its own USB device (pid 0x02CD) with the
        // same vendor serial as the dongle keyboard slot: switching modes
        // keeps one identity, so history and selection carry over.
        var wired = new HidDeviceReading(0x02CD, "Razer Joro", "SI2522F18701637", 255, 100, true);
        var wireless = new HidDeviceReading(0x00B8, "Razer Viper V3 HyperSpeed", "SI2522F18701637",
            255, 100, null, NameOverride: "Razer Joro", KindOverride: DeviceKind.Keyboard);
        Assert.Equal(HidWatcher.HandleFor(wireless), HidWatcher.HandleFor(wired));
    }

    [Fact]
    public void DeviceSlots_BleJoroIsKeyboardSlot()
    {
        // Bluetooth LE (VID 0x068E, PID 0x02CE): no vendor feature channel
        // exists on the report map — battery comes from the GATT Battery
        // Service; the slot entry documents the tx for a future firmware.
        var slots = RazerPidTable.DeviceSlots(0x02CE);
        Assert.Single(slots);
        Assert.Equal(RazerPidTable.SlotRole.Keyboard, slots[0].Role);
        Assert.Equal(BatteryScale.Scaled255, slots[0].Scale);
        Assert.Equal(new byte[] { 0x1F }, RazerPidTable.TransactionIdCandidates(0x02CE));
    }

    [Fact]
    public void BleMac_ExtractsAddressFromRealInterfacePath()
    {
        var blePath = @"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}_dev_vid&02068e_pid&02ce_rev&0001_cf4fcb85adf3&col01#c&186b2284&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        Assert.True(HidWatcher.IsBlePath(blePath));
        Assert.Equal(0xCF4FCB85ADF3ul, HidWatcher.BleMac(blePath));

        var usbPath = @"\\?\hid#vid_1532&pid_00b8&mi_01&col05#9&2c261f7&0&0004#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        Assert.False(HidWatcher.IsBlePath(usbPath));
        Assert.Null(HidWatcher.BleMac(usbPath));
    }

    [Fact]
    public void BleReading_MapsAndUsesMacIdentity()
    {
        // GATT battery is 0-100 percent; no DIS serial → the BLE MAC is the
        // identity, the Bluetooth device name is the display name.
        var reading = new HidDeviceReading(0x02CE, "", "BLE:CF4FCB85ADF3",
            100, 100, null, NameOverride: "Joro", KindOverride: DeviceKind.Keyboard);
        Assert.Equal("BLE:CF4FCB85ADF3", HidWatcher.HandleFor(reading));
        var device = HidWatcher.ToRazerDevice(reading, "BLE:CF4FCB85ADF3", shownHandle: "");
        Assert.Equal("Joro", device.Name);
        Assert.Equal(DeviceKind.Keyboard, device.Kind);
        Assert.Equal(100, device.BatteryPercentage);
        Assert.False(device.IsCharging); // GATT has no charging flag
    }

    [Fact]
    public void BleHeartbeat_ParsesIdentitiesFromSynapseLog()
    {
        // Real heartbeat shape (2026-09-07, Joro on BT + mouse on the dongle):
        // timestamp bracket, then a device array with useBle / powerStatus.
        var line = "[2026/09/07 14:17:44.410] info: Device  ["
            + @"{""serialNumber"":""632516H31000044"",""productId"":184,""hasBattery"":true,""useBle"":false,"
            + @"""category"":""MOUSE"",""name"":{""en"":""Razer Viper V3 HyperSpeed""},"
            + @"""powerStatus"":{""chargingStatus"":""NoCharge_BatteryFull"",""level"":61}},"
            + @"{""serialNumber"":""SI2522F18701637"",""productId"":717,""hasBattery"":true,""useBle"":true,"
            + @"""category"":""KEYBOARD"",""name"":{""en"":""Razer Joro"",""zh-cn"":""Razer 乔罗金蛛""},"
            + @"""powerStatus"":{""chargingStatus"":""Charging"",""level"":100},"
            + @"""note"":""bracket } inside \""a string\"""""
            + "}]";

        var now = new DateTime(2026, 9, 7, 14, 18, 0);
        var identities = RazerWatcher.ParseBleHeartbeat(line, now);
        var joro = Assert.Single(identities);
        Assert.Equal("SI2522F18701637", joro.Serial);
        Assert.Equal("Razer Joro", joro.Name);
        Assert.Equal("KEYBOARD", joro.Category);
        Assert.True(joro.Charging); // fresh heartbeat, chargingStatus=Charging

        // A stale heartbeat must not claim charging…
        var stale = RazerWatcher.ParseBleHeartbeat(line, now.AddHours(1));
        Assert.Null(Assert.Single(stale).Charging);

        // …and kind matching resolves the entry; two same-kind candidates stay ambiguous.
        Assert.Equal("SI2522F18701637", RazerWatcher.MatchBleIdentity(identities, DeviceKind.Keyboard)?.Serial);
        Assert.Null(RazerWatcher.MatchBleIdentity(
            new List<RazerWatcher.BleIdentity> { joro, joro with { Serial = "OTHER" } }, DeviceKind.Keyboard));
    }

    [Fact]
    public void ExtractJsonArray_SkipsTimestampAndHandlesEscapes()
    {
        Assert.Null(RazerWatcher.ExtractJsonArray("[2026/09/07 14:17:44.410] info: no array here"));
        Assert.Equal("""["a\"b"]""",
            RazerWatcher.ExtractJsonArray("""[ts] info: Device  ["a\"b"] tail"""));
    }

    [Fact]
    public void DeviceSlots_UnknownPidProbesAllSlots()
    {
        var slots = RazerPidTable.DeviceSlots(0x1234);
        Assert.Equal(new byte[] { 0x1F, 0x9F, 0x3F, 0xFF }, slots.Select(s => s.TransactionId).ToArray());
    }

    [Fact]
    public void DeviceSlots_KnownKeyboardPidKeepsKeyboardRole()
    {
        var slots = RazerPidTable.DeviceSlots(0x025C); // BlackWidow V3 Pro wireless
        Assert.Single(slots);
        Assert.Equal(RazerPidTable.SlotRole.Keyboard, slots[0].Role);
    }

    [Fact]
    public void ToRazerDevice_ComboKeyboardSlotOverridesNameAndKind()
    {
        // On a combo dongle the product string names the mouse; the keyboard
        // slot must not inherit it.
        var reading = new HidDeviceReading(0x00B8, "Razer Viper V3 HyperSpeed", "SI2522F18701637",
            255, 100, null, NameOverride: "Razer Keyboard", KindOverride: DeviceKind.Keyboard);
        var device = HidWatcher.ToRazerDevice(reading, "SI2522F18701637", shownHandle: "");
        Assert.Equal("Razer Keyboard", device.Name);
        Assert.Equal(DeviceKind.Keyboard, device.Kind);
        Assert.Equal(100, device.BatteryPercentage);
    }

    [Fact]
    public void ToRazerDevice_OwnDongleKeyboardKeepsProductName()
    {
        // On the keyboard's own dongle the product string is the keyboard's.
        var reading = new HidDeviceReading(0x0271, "Razer BlackWidow V3 Mini", "SN123456",
            200, 78, null, NameOverride: null, KindOverride: DeviceKind.Keyboard);
        var device = HidWatcher.ToRazerDevice(reading, "SN123456", shownHandle: "");
        Assert.Equal("Razer BlackWidow V3 Mini", device.Name);
        Assert.Equal(DeviceKind.Keyboard, device.Kind);
    }
}
