// Razer HID vendor protocol for battery queries, ported from the
// reverse-engineered OpenRazer kernel driver:
//   driver/razercommon.h       — the 90-byte `razer_report` layout (static_assert)
//   driver/razercommon.c       — CRC (plain XOR, no constant) + report plumbing
//   driver/razerchromacommon.c — battery 0x07/0x80, charging 0x07/0x84 builders
// Cross-checked against the Windows hidapi implementations
// xzeldon/razer-battery-report and atv57/opsrzr: hid.dll feature-report
// buffers carry a leading report-ID byte (0x00), so wire buffers are 91 bytes
// while the report payload stays 90.

namespace RazerTaskbar.Core;

/// <summary>Outcome of validating a response report. Busy/NoResponse are
/// retryable (the wireless link answers late); everything else is final for
/// the current attempt.</summary>
public enum RazerResponseKind
{
    Success,
    /// <summary>status 0x01 — device is still working on the command.</summary>
    Busy,
    /// <summary>status 0x04 — the wireless device did not answer in time.</summary>
    NoResponse,
    /// <summary>status 0x03 failure / 0x05 unsupported — do not retry.</summary>
    NotSupported,
    /// <summary>Response header does not match the request (stale answer from
    /// a previous command on a shared interface).</summary>
    BadEcho,
    BadCrc,
    BadLength,
}

public static class RazerReport
{
    public const ushort VendorId = 0x1532;

    /// <summary>The razer_report struct is exactly 90 bytes (OpenRazer
    /// `static_assert(sizeof(struct razer_report) == 90)`).</summary>
    public const int ReportSize = 90;

    /// <summary>hid.dll feature buffers = report-ID byte + the 90-byte
    /// report. The vendor collection defines no report ID, so byte 0 is 0.</summary>
    public const int HidBufferSize = ReportSize + 1;

    public const byte CommandClassBattery = 0x07;
    public const byte CommandGetBatteryLevel = 0x80;
    public const byte CommandGetChargingStatus = 0x84;
    public const byte DataSizeQuery = 0x02;

    /// <summary>Serial query (OpenRazer razer_chroma_standard_get_serial:
    /// class 0x00 / id 0x82, 22-byte argument = the ASCII serial).</summary>
    public const byte CommandClassInfo = 0x00;
    public const byte CommandGetSerial = 0x82;
    public const byte DataSizeSerial = 0x16;

    // razer_report payload offsets (status@0, remaining@2, protocol@4,
    // arguments@8..87, crc@88, reserved@89 — all zero for a query).
    private const int OffTransaction = 1;
    private const int OffDataSize = 5;
    private const int OffCommandClass = 6;
    private const int OffCommandId = 7;
    private const int OffArguments = 8;
    private const int OffCrc = 88;
    private const int HidShift = 1; // hid.dll buffer = payload + report-ID byte

    /// <summary>Build a get-command request as an hid.dll buffer (91 bytes,
    /// zero-initialized: status NEW_COMMAND, remaining/protocol/args 0).</summary>
    public static byte[] BuildQuery(byte transactionId, byte commandClass, byte commandId, byte dataSize = DataSizeQuery)
    {
        var buf = new byte[HidBufferSize];
        buf[HidShift + OffTransaction] = transactionId;
        buf[HidShift + OffDataSize] = dataSize;
        buf[HidShift + OffCommandClass] = commandClass;
        buf[HidShift + OffCommandId] = commandId;
        buf[HidShift + OffCrc] = CalculateCrc(buf);
        return buf;
    }

    public static byte[] BuildBatteryQuery(byte transactionId)
        => BuildQuery(transactionId, CommandClassBattery, CommandGetBatteryLevel);

    public static byte[] BuildChargingQuery(byte transactionId)
        => BuildQuery(transactionId, CommandClassBattery, CommandGetChargingStatus);

    public static byte[] BuildSerialQuery(byte transactionId)
        => BuildQuery(transactionId, CommandClassInfo, CommandGetSerial, DataSizeSerial);

    /// <summary>Extract the 22-byte ASCII serial from a serial response.
    /// Devices occasionally emit garbage (OpenRazer issue #2122): anything
    /// non-printable makes the whole answer invalid.</summary>
    public static bool TryGetSerial(ReadOnlySpan<byte> hidBuffer, byte transactionId, out string serial)
    {
        serial = "";
        if (ParseResponse(hidBuffer, transactionId, CommandClassInfo, CommandGetSerial, out _)
            != RazerResponseKind.Success)
        {
            return false;
        }
        var chars = new char[DataSizeSerial];
        int len = 0;
        for (int i = 0; i < DataSizeSerial; i++)
        {
            byte b = hidBuffer[HidShift + OffArguments + i];
            if (b == 0)
            {
                break;
            }
            if (b < 0x20 || b > 0x7e)
            {
                return false;
            }
            chars[len++] = (char)b;
        }
        if (len < 6)
        {
            return false; // too short to be a Razer serial
        }
        serial = new string(chars, 0, len);
        return true;
    }

    /// <summary>OpenRazer `razer_calculate_crc`: XOR payload bytes 2..87
    /// together (status and transaction id excluded, no constant). Reads only
    /// the payload window, so a full hid.dll buffer works as input.</summary>
    public static byte CalculateCrc(ReadOnlySpan<byte> hidBuffer)
    {
        byte crc = 0;
        for (int i = HidShift + 2; i <= HidShift + 87; i++)
        {
            crc ^= hidBuffer[i];
        }
        return crc;
    }

    /// <summary>Validate a received feature report. Status is checked before
    /// the echo: busy/no-response answers may still carry the previous
    /// command's header. On Success `argument1` holds arguments[1] (battery
    /// level raw, or the charging flag).</summary>
    public static RazerResponseKind ParseResponse(
        ReadOnlySpan<byte> hidBuffer, byte transactionId, byte commandClass, byte commandId, out byte argument1)
    {
        argument1 = 0;
        if (hidBuffer.Length < HidBufferSize)
        {
            return RazerResponseKind.BadLength;
        }
        switch (hidBuffer[HidShift + 0])
        {
            case 0x02: break; // SUCCESS
            case 0x01: return RazerResponseKind.Busy;
            case 0x04: return RazerResponseKind.NoResponse;
            default: return RazerResponseKind.NotSupported; // 0x03 failure / 0x05 unsupported
        }
        if (hidBuffer[HidShift + OffTransaction] != transactionId
            || hidBuffer[HidShift + OffCommandClass] != commandClass
            || hidBuffer[HidShift + OffCommandId] != commandId)
        {
            return RazerResponseKind.BadEcho;
        }
        if (hidBuffer[HidShift + OffCrc] != CalculateCrc(hidBuffer))
        {
            return RazerResponseKind.BadCrc;
        }
        argument1 = hidBuffer[HidShift + OffArguments + 1];
        return RazerResponseKind.Success;
    }

    /// <summary>How a battery level raw byte maps to a percentage. Firmware
    /// generations disagree: legacy (~2019-) devices report 0..255, newer
    /// devices report 0..100 directly (OpenRazer carries a per-PID switch
    /// for exactly this).</summary>
    public static int LevelPercent(byte raw, BatteryScale scale)
    {
        return scale switch
        {
            BatteryScale.Scaled255 => raw * 100 / 255,
            BatteryScale.Direct100 => Math.Min(100, (int)raw),
            // Unknown generation: values above 100 can only be the 0..255
            // scale; small values are ambiguous and assumed direct.
            _ => raw <= 100 ? Math.Min(100, (int)raw) : raw * 100 / 255,
        };
    }
}

public enum BatteryScale
{
    /// <summary>Raw byte is the percentage (0..100) as-is.</summary>
    Direct100,
    /// <summary>Raw byte is 0..255, scale by ×100/255 (legacy firmware).</summary>
    Scaled255,
    /// <summary>Heuristic: ≤100 taken as direct percent, larger as 0..255.</summary>
    Auto,
}
