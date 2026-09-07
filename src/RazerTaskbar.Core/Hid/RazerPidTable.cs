// Per-PID transaction ids for the battery query, ported from the OpenRazer
// driver switches (razermouse_driver.c / razerkbd_driver.c `charge_level` /
// `charge_status`). Old firmware (~2019-) silently ignores queries with a
// wrong transaction id, so known devices go straight to their id; unknown
// PIDs probe the fallback list — newer generations accept any id, validated
// by echo + CRC.

namespace RazerTaskbar.Core;

public static class RazerPidTable
{
    public readonly record struct DeviceTx(byte TransactionId, BatteryScale Scale);

    /// <summary>Known devices. Scale: mice report the raw byte on a 0..255
    /// scale (confirmed on a Viper V3 HyperSpeed — new and legacy generations
    /// alike, matching OpenRazer). Keyboards are unverified; Auto lets the
    /// heuristic in RazerReport.LevelPercent cover both conventions.</summary>
    private static readonly Dictionary<int, DeviceTx> Known = new()
    {
        // — mice, new generation (tx 0x1F) —
        [0x00B8] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Viper V3 HyperSpeed (confirmed: raw 161 = 63%)
        [0x00A5] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Viper V2 Pro wired
        [0x00A6] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Viper V2 Pro wireless
        [0x00C0] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Viper V3 Pro wired
        [0x00C1] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Viper V3 Pro wireless
        [0x00B6] = new DeviceTx(0x1F, BatteryScale.Scaled255), // DeathAdder V3 Pro wired
        [0x00B7] = new DeviceTx(0x1F, BatteryScale.Scaled255), // DeathAdder V3 Pro wireless
        [0x00C2] = new DeviceTx(0x1F, BatteryScale.Scaled255), // DeathAdder V3 Pro ALT wired
        [0x00C3] = new DeviceTx(0x1F, BatteryScale.Scaled255), // DeathAdder V3 Pro ALT wireless
        [0x00BE] = new DeviceTx(0x1F, BatteryScale.Scaled255), // DeathAdder V4 Pro wired
        [0x00BF] = new DeviceTx(0x1F, BatteryScale.Scaled255), // DeathAdder V4 Pro wireless
        [0x00AA] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Basilisk V3 Pro
        [0x00AB] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Basilisk V3 Pro wireless
        [0x00CC] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Basilisk V3 Pro 35K
        [0x00CD] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Basilisk V3 Pro 35K wireless
        [0x00D6] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Basilisk V3 Pro Phantom Green
        [0x00D7] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Basilisk V3 Pro PG wireless
        [0x008F] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Naga Pro
        [0x0090] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Naga Pro wireless
        [0x00A7] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Naga V2 Pro
        [0x00A8] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Naga V2 Pro wireless
        [0x0094] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Orochi V2 receiver
        [0x0095] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Orochi V2 bluetooth (charging n/a)
        [0x00AF] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Cobra Pro
        [0x00B0] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Cobra Pro wireless
        [0x0077] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Pro Click
        [0x0080] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Pro Click wireless
        [0x00D0] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Pro Click V2
        [0x00D1] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Pro Click V2 wireless
        [0x009A] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Pro Click Mini receiver
        [0x00B3] = new DeviceTx(0x1F, BatteryScale.Scaled255), // HyperPolling Wireless Dongle

        // — mice, legacy generation (percent = raw × 100 / 255) —
        [0x007C] = new DeviceTx(0x3F, BatteryScale.Scaled255), // DeathAdder V2 Pro wired
        [0x007D] = new DeviceTx(0x3F, BatteryScale.Scaled255), // DeathAdder V2 Pro wireless
        [0x0072] = new DeviceTx(0x3F, BatteryScale.Scaled255), // Mamba Wireless receiver
        [0x0073] = new DeviceTx(0x3F, BatteryScale.Scaled255), // Mamba Wireless wired
        [0x007A] = new DeviceTx(0xFF, BatteryScale.Scaled255), // Viper Ultimate wired
        [0x007B] = new DeviceTx(0xFF, BatteryScale.Scaled255), // Viper Ultimate wireless

        // — keyboards (only wireless-capable ones carry a battery) —
        [0x025A] = new DeviceTx(0x3F, BatteryScale.Auto), // BlackWidow V3 Pro wired
        [0x025C] = new DeviceTx(0x9F, BatteryScale.Auto), // BlackWidow V3 Pro wireless
        [0x0258] = new DeviceTx(0x1F, BatteryScale.Auto), // BlackWidow V3 Mini HS wired
        [0x0271] = new DeviceTx(0x9F, BatteryScale.Auto), // BlackWidow V3 Mini HS wireless
    };

    /// <summary>Probe order for PIDs missing from the table: new-gen mice,
    /// then wireless keyboards, then the legacy groups.</summary>
    public static readonly byte[] FallbackTransactionIds = { 0x1F, 0x9F, 0x3F, 0xFF };

    /// <summary>Which wireless sub-device a transaction id addresses. On
    /// multi-device combo dongles the mouse and the paired keyboard share one
    /// vendor feature channel: the dongle routes by transaction id
    /// (confirmed on a Viper V3 HyperSpeed + Joro combo: 0x1F → mouse,
    /// 0x9F → keyboard "SI2522F18701637", raw 255 = 100%).</summary>
    public enum SlotRole { Mouse, Keyboard }

    public readonly record struct DeviceSlot(byte TransactionId, SlotRole Role, BatteryScale Scale);

    private static readonly DeviceSlot[] FallbackSlots =
    {
        new DeviceSlot(0x1F, SlotRole.Mouse, BatteryScale.Auto),
        new DeviceSlot(0x9F, SlotRole.Keyboard, BatteryScale.Scaled255),
        new DeviceSlot(0x3F, SlotRole.Mouse, BatteryScale.Auto),
        new DeviceSlot(0xFF, SlotRole.Mouse, BatteryScale.Auto),
    };

    /// <summary>All wireless slots to sweep for a PID. Multi-device dongles
    /// yield one slot per paired sub-device; serial-keyed dedup collapses
    /// slots that answer for the same physical device.</summary>
    public static IReadOnlyList<DeviceSlot> DeviceSlots(int pid)
    {
        if (pid == 0x00B8)
        {
            return new DeviceSlot[]
            {
                new(0x1F, SlotRole.Mouse, BatteryScale.Scaled255),
                new(0x9F, SlotRole.Keyboard, BatteryScale.Scaled255),
            };
        }
        if (Known.TryGetValue(pid, out var tx))
        {
            return new[] { new DeviceSlot(tx.TransactionId,
                tx.TransactionId == 0x9F ? SlotRole.Keyboard : SlotRole.Mouse, tx.Scale) };
        }
        return FallbackSlots;
    }

    public static bool TryGetDevice(int pid, out DeviceTx tx) => Known.TryGetValue(pid, out tx);

    /// <summary>Ids to try for a PID, known ones as a single candidate,
    /// unknown ones the full fallback list.</summary>
    public static byte[] TransactionIdCandidates(int pid)
        => Known.TryGetValue(pid, out var tx) ? new[] { tx.TransactionId } : FallbackTransactionIds;

    public static BatteryScale ScaleFor(int pid)
        => Known.TryGetValue(pid, out var tx) ? tx.Scale : BatteryScale.Auto;
}
