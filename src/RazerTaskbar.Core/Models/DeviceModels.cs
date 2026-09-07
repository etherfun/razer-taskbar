// Port of src/battery.rs: device model + display-pick rule
// (originally `tray_manager.ts:pickDeviceToDisplay`).

namespace RazerTaskbar.Core;

/// <summary>What kind of peripheral a device is (drives the type icon).
/// Synapse V4 logs carry an explicit `category`; V3 logs fall back to
/// product-name keywords.</summary>
public enum DeviceKind
{
    Headset,
    Mouse,
    Keyboard,
    Other,
}

public sealed record RazerDevice(
    string Name,
    string Handle,
    int BatteryPercentage,
    bool IsCharging,
    /// <summary>Device-side battery saver / low-power mode (Synapse `lowPowerMode`).</summary>
    bool BatterySaver,
    bool IsConnected,
    bool IsSelected,
    DeviceKind Kind);

public static class DeviceClassifier
{
    /// <summary>Classify a device from its Synapse V4 `category` (may be empty —
    /// V3 logs have no category), falling back to product-name keywords.</summary>
    public static DeviceKind FromCategoryAndName(string category, string name)
    {
        switch (category.ToUpperInvariant())
        {
            case "MOUSE": return DeviceKind.Mouse;
            case "KEYBOARD": return DeviceKind.Keyboard;
            case "HEADSET":
            case "HEADPHONES":
            case "EARBUDS": return DeviceKind.Headset;
        }

        var n = name.ToLowerInvariant();
        // Order matters only where a name could hit two lists; the lists are
        // disjoint. First hit wins within each list.
        if (ContainsAny(n, "headset", "headphone", "earbud", "kraken", "blackshark", "barracuda", "opus", "hammerhead"))
        {
            return DeviceKind.Headset;
        }
        if (ContainsAny(n, "keyboard", "huntsman", "blackwidow", "ornata", "cynosa", "deathstalker", "tartarus", "joro"))
        {
            return DeviceKind.Keyboard;
        }
        if (ContainsAny(n, "mouse", "viper", "deathadder", "basilisk", "naga", "mamba", "orochi", "lancehead", "diamondback", "cobra", "adder", "orca", "atheris"))
        {
            return DeviceKind.Mouse;
        }
        return DeviceKind.Other;
    }

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        foreach (var k in needles)
        {
            if (haystack.Contains(k))
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>Listing labels that disambiguate same-name devices — two units
/// of one model (original + replacement), or one device split across
/// identities (HID fallback `HID:{pid}` vs Synapse serial). Rendered as
/// "Razer Mouse(31000044)"; the tag only appears when the name actually
/// collides within the listing, so single-device listings stay clean.</summary>
public static class DeviceLabels
{
    private const int TagLength = 8;

    public static string Label(string name, string handle, IReadOnlyCollection<string> listingNames)
    {
        int count = 0;
        foreach (var n in listingNames)
        {
            if (n == name)
            {
                count++;
            }
        }
        if (count < 2 || handle.Length == 0)
        {
            return name;
        }
        // Serial handles shorten to their tail; the synthesized HID:{pid}
        // form is short enough to show whole.
        var tag = handle.Contains(':') ? handle : handle[^Math.Min(TagLength, handle.Length)..];
        return $"{name}({tag})";
    }
}

public static class DeviceSelector
{
    /// <summary>Pick the user-selected device, else the lowest battery preferring
    /// non-charging. Mirrors `a.battery * (a.charging ? 100 : 1)` ordering.</summary>
    public static RazerDevice? PickDeviceToDisplay(IReadOnlyDictionary<string, RazerDevice> devices)
    {
        RazerDevice? best = null;
        long bestKey = long.MaxValue;
        foreach (var d in devices.Values)
        {
            if (!d.IsConnected || !d.IsSelected)
            {
                continue;
            }
            long key = d.BatteryPercentage * (d.IsCharging ? 100 : 1);
            if (key < bestKey)
            {
                bestKey = key;
                best = d;
            }
        }
        return best;
    }
}
