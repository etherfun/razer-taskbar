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
        [0x02CD] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Razer Joro wired (confirmed: raw 255 = 100%, charging 0x84 works)
        [0x02CE] = new DeviceTx(0x1F, BatteryScale.Scaled255), // Razer Joro BLE (no vendor channel on BT; battery via GATT)
    };

    /// <summary>Probe order for PIDs missing from the table: new-gen mice,
    /// then wireless keyboards, then the legacy groups.</summary>
    public static readonly byte[] FallbackTransactionIds = { 0x1F, 0x9F, 0x3F, 0xFF };

    /// <summary>PIDs that enumerate the <em>device itself</em> over USB (its
    /// cable mode) — as opposed to a receiver/dongle or the Bluetooth stack.
    /// On such a link the device is physically powered by the cable, so a
    /// full battery with no charge flag still means "on USB power"
    /// (Joro 0x02CD verified: flag drops to 0 at 100% while cabled).
    /// Receiver slots and BLE must never take this shortcut: there the
    /// device sits on its battery at 100%.</summary>
    private static readonly HashSet<int> WiredDevicePids = new()
    {
        0x02CD, // Razer Joro cable mode (verified)
        0x025A, // BlackWidow V3 Pro wired
        0x0258, // BlackWidow V3 Mini HS wired
        0x007C, // DeathAdder V2 Pro wired
        0x0073, // Mamba Wireless wired
        0x007A, // Viper Ultimate wired
        0x00C2, // DeathAdder V3 Pro ALT wired
        0x00BE, // DeathAdder V4 Pro wired
    };

    /// <summary>True when the PID enumerates the device itself (cable mode)
    /// rather than a receiver or Bluetooth link.</summary>
    public static bool IsWiredDevice(int pid) => WiredDevicePids.Contains(pid);

    /// <summary>USB HID transport of this pid: cable mode when the pid
    /// enumerates the device itself, a 2.4G receiver otherwise (an unknown
    /// Razer pid that enumerates over USB is a dongle far more often than a
    /// cabled device — cable-mode pids are the enumerated list above). Both
    /// rank as the USB tier; the split is kept because it is recorded per
    /// device (see <see cref="BatteryTransport"/>).</summary>
    public static BatteryTransport UsbTransport(int pid)
        => IsWiredDevice(pid) ? BatteryTransport.Wired : BatteryTransport.Receiver;

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

    /// <summary>PIDs whose slot list is fully known (combo dongles, or
    /// devices whose transaction id does not follow the mouse/keyboard
    /// convention). Checked before the Known table.</summary>
    private static readonly Dictionary<int, DeviceSlot[]> ExplicitSlots = new()
    {
        // Viper V3 HyperSpeed combo dongle: the paired keyboard answers on
        // the mouse's vendor channel, routed by transaction id.
        [0x00B8] = new DeviceSlot[]
        {
            new(0x1F, SlotRole.Mouse, BatteryScale.Scaled255),
            new(0x9F, SlotRole.Keyboard, BatteryScale.Scaled255),
        },
        // Razer Joro in cable mode: an independent USB device (the wireless
        // link is down), tx 0x1F despite being a keyboard — the vendor
        // channel answers with the same serial as the dongle slot
        // (SI…, raw 255 = 100%), so both modes share one identity.
        [0x02CD] = new DeviceSlot[] { new(0x1F, SlotRole.Keyboard, BatteryScale.Scaled255) },
        // Razer Joro over Bluetooth LE (VID 0x068E): the report map has no
        // vendor feature channel, so the battery comes from the GATT Battery
        // Service (BleBattery); the slot entry documents the tx for the day
        // a firmware exposes vendor features over BT.
        [0x02CE] = new DeviceSlot[] { new(0x1F, SlotRole.Keyboard, BatteryScale.Scaled255) },
    };

    /// <summary>All wireless slots to sweep for a PID. Multi-device dongles
    /// yield one slot per paired sub-device; serial-keyed dedup collapses
    /// slots that answer for the same physical device.</summary>
    public static IReadOnlyList<DeviceSlot> DeviceSlots(int pid)
    {
        if (ExplicitSlots.TryGetValue(pid, out var slots))
        {
            return slots;
        }
        if (Known.TryGetValue(pid, out var tx))
        {
            return new[] { new DeviceSlot(tx.TransactionId,
                tx.TransactionId == 0x9F ? SlotRole.Keyboard : SlotRole.Mouse, tx.Scale) };
        }
        return FallbackSlots;
    }

    /// <summary>Discriminator for the synthesized identity of a slot that
    /// serves a SECOND sub-device of its dongle — one whose role differs from
    /// the pid's primary slot — or null for slots that alias the primary
    /// sub-device (`HID:{pid}` stays their fallback). The pid names only one
    /// sub-device, so without the key the sibling's readings share its
    /// DeviceStore entry and history series (see HidWatcher.HandleFor).</summary>
    public static string? SecondaryKey(int pid, DeviceSlot slot)
    {
        var slots = DeviceSlots(pid);
        if (slots.Count <= 1 || slots[0].Role == slot.Role)
        {
            return null;
        }
        return slot.Role == SlotRole.Keyboard ? "K" : "M";
    }

    public static bool TryGetDevice(int pid, out DeviceTx tx) => Known.TryGetValue(pid, out tx);

    /// <summary>Ids to try for a PID, known ones as a single candidate,
    /// unknown ones the full fallback list.</summary>
    public static byte[] TransactionIdCandidates(int pid)
        => Known.TryGetValue(pid, out var tx) ? new[] { tx.TransactionId } : FallbackTransactionIds;

    public static BatteryScale ScaleFor(int pid)
        => Known.TryGetValue(pid, out var tx) ? tx.Scale : BatteryScale.Auto;
}
