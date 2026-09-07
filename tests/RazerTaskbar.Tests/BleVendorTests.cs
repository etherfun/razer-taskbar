// Port coverage for the Razer vendor BLE GATT channel (BleVendor): the
// command/response protocol was reverse engineered from HCI captures of
// Synapse talking to a Joro (docs/agent-hid.md). The golden frames here are
// byte-exact from those captures and from live probe replays.

using RazerTaskbar.Core;
using Xunit;

namespace RazerTaskbar.Tests;

public class BleVendorTests
{
    [Fact]
    public void BuildCommand_QueryMatchesCapturedSynapseFrame()
    {
        // Synapse's battery query on the wire: seq 05, page 05 id 81 param 0001.
        var frame = BleVendor.BuildCommand(0x05, 0x00, 0x05, 0x81, 0x0001);
        Assert.Equal(new byte[] { 0x05, 0x00, 0x00, 0x00, 0x05, 0x81, 0x00, 0x01 }, frame);
    }

    [Fact]
    public void BuildCommand_SetMatchesCapturedSynapseFrame()
    {
        // Synapse's brightness set: seq 10, payload_len 1, page 10 id 05
        // param 0100, payload byte 00 sent as a second write.
        var frame = BleVendor.BuildCommand(0x10, 0x01, 0x10, 0x05, 0x0100);
        Assert.Equal(new byte[] { 0x10, 0x01, 0x00, 0x00, 0x10, 0x05, 0x01, 0x00 }, frame);
    }

    [Fact]
    public void ParseHeader_AcceptsOkHeaderWithPayloadLength()
    {
        // Serial read response header: echo 01, len 0x16 (22 payload bytes).
        var frame = Convert.FromHexString("0116000000000002383730313633370000000000");
        Assert.Equal((22, (byte)0x02), BleVendor.ParseHeader(frame, 0x01));
    }

    [Fact]
    public void ParseHeader_AcceptsUnknownCommandError()
    {
        // Get-half sweep on an unsupported id: tag 0x05, no payload.
        var frame = Convert.FromHexString("0100000000000005CA3730313633370000000000");
        Assert.Equal((0, (byte)0x05), BleVendor.ParseHeader(frame, 0x01));
    }

    [Fact]
    public void ParseHeader_RejectsForeignSeqAndShortFrames()
    {
        var header = Convert.FromHexString("0216000000000002383730313633370000000000");
        Assert.Null(BleVendor.ParseHeader(header, 0x01));

        // A data snapshot happens to start with the echo byte — must not
        // parse as a header once the response is underway.
        var data = Convert.FromHexString("0100000000000002383730313633370000000000");
        Assert.Equal((0, (byte)0x02), BleVendor.ParseHeader(data, 0x01));
        Assert.Null(BleVendor.ParseHeader(data[..7], 0x01));
    }

    [Fact]
    public void ReassemblePayload_SerialSpansTwoRegisterSnapshots()
    {
        // (01,83) serial read: 22 bytes stream as a full 20-byte register
        // snapshot plus a 2-byte remainder; the tail of each snapshot keeps
        // stale register content which must be dropped.
        var full = Convert.FromHexString("5349323532324631383730313633370000000000");
        var rest = Convert.FromHexString("00003235323246313837303136333700000000");
        var payload = BleVendor.ReassemblePayload(22, new[] { full, rest });
        Assert.Equal(22, payload.Length);
        Assert.Equal("SI2522F18701637", System.Text.Encoding.ASCII.GetString(payload[..15]));
        Assert.All(payload[15..], b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(0x00, 0)]
    [InlineData(0xF7, 97)] // live Joro reading while the keyboard's own BAS says 97
    [InlineData(0xF9, 98)] // rose two ticks after a minute on the charger
    [InlineData(0x9C, 61)] // wired probe saw 156 → 61 on the same formula
    [InlineData(0xFF, 100)]
    public void BatteryPercent_MatchesScaled255(byte raw, int expected)
    {
        Assert.Equal(expected, BleVendor.BatteryPercent(raw));
    }
}
