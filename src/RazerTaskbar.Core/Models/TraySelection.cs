// Multi-device tray icons: the pure half of `tray_device_handles` — resolve the
// configured handles against the device store (unknown handles skipped, order
// preserved) and format the tooltip shared by the single legacy icon and every
// per-device icon. No Win32 here, so both rules are unit-tested without
// referencing the WinUI exe.

namespace RazerTaskbar.Core;

public static class TraySelection
{
    /// <summary>Devices that get their own tray icon, in the configured
    /// selection order. Handles the store does not currently know are skipped
    /// (a device may be briefly absent during an identity change); duplicate
    /// handles are collapsed. An empty result is the legacy sentinel: one icon
    /// following <see cref="DisplayModeResolver.Pick"/> — that also covers an
    /// all-unknown selection, so the app never ends up with no icon at all.</summary>
    public static List<RazerDevice> Resolve(
        IReadOnlyList<string>? handles,
        IReadOnlyDictionary<string, RazerDevice> devices)
    {
        var selected = new List<RazerDevice>();
        if (handles is null || handles.Count == 0 || devices.Count == 0)
        {
            return selected;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in handles)
        {
            if (string.IsNullOrEmpty(handle)
                || !seen.Add(handle)
                || !devices.TryGetValue(handle, out var device))
            {
                continue;
            }
            selected.Add(device);
        }
        return selected;
    }

    /// <summary>Tooltip text of one icon. The charging marker only appears for
    /// a connected device (a stale flag on an offline row must not claim it is
    /// charging) and the usage estimate only when `estimate` is supplied —
    /// callers pass one for connected devices only, since predictions need a
    /// live discharge anchor. `Name: 53%` + `offline` for a recorded device
    /// that is not online.</summary>
    public static string Tooltip(RazerDevice device, string? estimate)
    {
        var charging = device.IsCharging && device.IsConnected ? $" {I18n.Tr("(charging)")}" : "";
        var offline = device.IsConnected ? "" : $" {I18n.Tr("offline")}";
        var tip = $"{device.Name}: {device.BatteryPercentage}%{charging}{offline}";
        if (device.IsConnected && estimate is { Length: > 0 } suffix)
        {
            tip += $" · {suffix}";
        }
        return tip;
    }
}
