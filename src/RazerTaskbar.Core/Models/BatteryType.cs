// Battery type: how a peripheral is powered. Port of the split openrazer makes
// in `driver/razermouse_driver.c: razer_attr_read_charge_status`, where a
// hard-coded PID list of Atheris / Orochi / HyperSpeed-class models is
// short-circuited to "0" with the comment "Wireless mice that don't support
// is_charging / Use AA batteries" — openrazer classifies a device's battery by
// MODEL, not by a value read off the device. Every other device with a
// charge_level answers a real charge-status report.
//
// Why the distinction matters here: a replaceable cell can only jump upward
// when the user swaps it, so a rise is NOT a charge session — the
// discharge/charge split, the per-full-charge statistic and the
// capacity-fade (battery health) estimate would all read a swap as a charge.

namespace RazerTaskbar.Core;

/// <summary>How a device's battery is powered.</summary>
public enum BatteryType
{
    /// <summary>Not configured — the model-name table decides (<see cref="BatteryTypes.Detect"/>).</summary>
    Auto,
    /// <summary>Built-in rechargeable pack: charges on a dock/cable.</summary>
    Rechargeable,
    /// <summary>User-replaceable AA/AAA cell: never charges, a rise is a swap.</summary>
    Replaceable,
}

public static class BatteryTypes
{
    /// <summary>Model-name fragments of the AA/AAA-powered family. Ported from
    /// openrazer's `charge_status → 0` PID list (Razer Atheris, Orochi V2,
    /// Basilisk X / V3 X HyperSpeed, Basilisk Mobile, DeathAdder V2 X
    /// HyperSpeed, Naga V2 HyperSpeed, Viper V3 HyperSpeed) plus the Pro Click
    /// Mini, which openrazer does not drive. Matched case-insensitively
    /// against the Synapse product name; the HyperSpeed names are spelled out
    /// so neither the rechargeable siblings (Viper V3 Pro, Naga V2 Pro, ...)
    /// nor the HyperSpeed KEYBOARDS (built-in pack) can match.</summary>
    private static readonly string[] ReplaceableModels =
    [
        "atheris",
        // Every Orochi is AA-powered (2011/2013/V2); the wired Chroma variant
        // has no meaningful battery reading anyway.
        "orochi",
        "basilisk x hyperspeed",
        "basilisk v3 x hyperspeed",
        "basilisk mobile",
        "deathadder v2 x",
        "naga v2 hyperspeed",
        "viper v3 hyperspeed",
        "pro click mini",
    ];

    /// <summary>Classify by product name. Recognised AA/AAA models are
    /// <see cref="BatteryType.Replaceable"/>; everything else (including an
    /// unknown/empty name) is treated as a rechargeable pack — the same
    /// default openrazer gets from its charge-status switch.</summary>
    public static BatteryType Detect(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BatteryType.Rechargeable;
        }
        var n = name.ToLowerInvariant();
        foreach (var k in ReplaceableModels)
        {
            if (n.Contains(k))
            {
                return BatteryType.Replaceable;
            }
        }
        return BatteryType.Rechargeable;
    }

    /// <summary>The explicit setting when there is one, else the model table.</summary>
    public static BatteryType Resolve(BatteryType configured, string? name)
        => configured == BatteryType.Auto ? Detect(name) : configured;

    /// <summary>settings.json spelling (`device_battery_types` values).</summary>
    public static string ToConfig(this BatteryType t) => t switch
    {
        BatteryType.Rechargeable => "rechargeable",
        BatteryType.Replaceable => "replaceable",
        _ => "auto",
    };

    /// <summary>Parse <see cref="ToConfig"/>; anything unrecognized (including
    /// null and an unknown spelling from a hand-edited file) means auto.</summary>
    public static BatteryType Parse(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "rechargeable" => BatteryType.Rechargeable,
        "replaceable" => BatteryType.Replaceable,
        _ => BatteryType.Auto,
    };

    /// <summary>The series as a replaceable-battery device sees it: the charge
    /// flag forced off. Such a device has no charging hardware, so a charging
    /// status it still reports (or the log parser infers) is noise that would
    /// mint fake charge sessions and poison the charge-rate statistics.</summary>
    public static List<Sample> AsReplaceable(IReadOnlyList<Sample> samples)
        => samples.Select(s => s with { Charging = false }).ToList();
}
