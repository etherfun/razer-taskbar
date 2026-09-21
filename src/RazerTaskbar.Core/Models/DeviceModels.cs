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

/// <summary>Which link a device's battery reading actually came in on. The
/// read priority is declared by <see cref="BatteryTransports.Priority"/>: a
/// cabled device and one sitting on its 2.4G receiver both speak USB HID and
/// share a tier, above a Bluetooth-LE link, above the Synapse log
/// fallback.</summary>
public enum BatteryTransport
{
    /// <summary>USB HID in cable mode — the PID enumerates the device itself
    /// (see <c>RazerPidTable.IsWiredDevice</c>).</summary>
    Wired,
    /// <summary>USB HID through a 2.4G receiver/dongle.</summary>
    Receiver,
    /// <summary>Bluetooth LE: Razer's vendor GATT channel when it is free,
    /// the plain Battery Service otherwise.</summary>
    Ble,
    /// <summary>Synapse log parsing — the last-resort source.</summary>
    Log,
}

public static class BatteryTransports
{
    /// <summary>Read priority: lower wins. Wired and receiver deliberately
    /// share tier 0 (有线usb = 2.4G接收器), so neither displaces the other —
    /// only BLE and the log sit below them.</summary>
    public static int Priority(BatteryTransport t) => t switch
    {
        BatteryTransport.Wired or BatteryTransport.Receiver => 0,
        BatteryTransport.Ble => 1,
        _ => 2,
    };

    /// <summary>True when `candidate` is the better source of the two.</summary>
    public static bool Outranks(BatteryTransport candidate, BatteryTransport incumbent)
        => Priority(candidate) < Priority(incumbent);
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
    DeviceKind Kind,
    /// <summary>Link this reading came in on — recorded per device and
    /// persisted by the history store. Defaults to the log source: every
    /// producer that does not read a device directly is the log parser.</summary>
    BatteryTransport Transport = BatteryTransport.Log);

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
