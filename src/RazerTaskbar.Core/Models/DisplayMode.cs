// Multi-device display strategies layered on DeviceSelector: the plain pick
// (fixed), a temporary swap to any device the moment its battery drops
// (drop_swap), and a timed carousel over all connected devices (rotate).
// Pure and unit-tested (DisplayModeTests); the mutable state lives on the
// widget thread only, and every tick is timestamp-idempotent so the widget
// paint and the tray refresh can share one state.

namespace RazerTaskbar.Core;

public enum DisplayMode
{
    Fixed,
    DropSwap,
    Rotate,
}

/// <summary>Widget-thread runtime state for the non-fixed display modes.
/// Survives for the process lifetime; reset when the mode or its tuning
/// changes so the new strategy starts from a clean slate.</summary>
public sealed class DisplayModeState
{
    /// <summary>drop_swap: device currently replacing the shown one ("" = none).</summary>
    public string OverrideHandle = "";

    /// <summary>drop_swap: TickCount64 ms when the swap window ends.</summary>
    public long OverrideUntilMs;

    /// <summary>rotate: current position in the name-ordered device list.</summary>
    public int RotateIndex;

    /// <summary>rotate: TickCount64 ms of the last advance (0 = not started).</summary>
    public long LastRotateMs;

    /// <summary>drop_swap: last seen battery level per handle, to detect drops.</summary>
    public Dictionary<string, int> LastBattery = new();

    /// <summary>Handle the resolver last returned — lets read-only consumers
    /// (hover panel highlight) follow the mode without advancing the state.</summary>
    public string LastShownHandle = "";

    public void Reset()
    {
        OverrideHandle = "";
        OverrideUntilMs = 0;
        RotateIndex = 0;
        LastRotateMs = 0;
        LastBattery.Clear();
    }
}

public static class DisplayModeResolver
{
    public static DisplayMode Parse(string mode) => mode switch
    {
        "drop_swap" => DisplayMode.DropSwap,
        "rotate" => DisplayMode.Rotate,
        _ => DisplayMode.Fixed,
    };

    /// <summary>Resolve the device to show under the configured mode. `nowMs`
    /// is Environment.TickCount64. Advancing is guarded by timestamps, so
    /// multiple calls within one interval are side-effect-free replays.</summary>
    public static RazerDevice? Pick(
        IReadOnlyDictionary<string, RazerDevice> devices,
        Config cfg,
        DisplayModeState st,
        long nowMs)
    {
        var device = Parse(cfg.DisplayMode) switch
        {
            DisplayMode.DropSwap => PickDropSwap(devices, cfg.SwapDisplaySecs, st, nowMs),
            DisplayMode.Rotate => PickRotate(devices, cfg.RotateIntervalSecs, st, nowMs),
            _ => DeviceSelector.PickDeviceToDisplay(devices),
        };
        st.LastShownHandle = device?.Handle ?? "";
        return device;
    }

    /// <summary>Shown device, except while some device just lost battery
    /// level: any strict drop (100→99 counts) swaps it in for swapSecs. The
    /// freshest drop wins — including the shown device's own, which falls
    /// back to it. A drop during the window switches/re-arms; expiry or
    /// disconnect falls back.</summary>
    private static RazerDevice? PickDropSwap(
        IReadOnlyDictionary<string, RazerDevice> devices,
        ulong swapSecs,
        DisplayModeState st,
        long nowMs)
    {
        var baseDevice = DeviceSelector.PickDeviceToDisplay(devices);
        if (st.OverrideHandle.Length > 0
            && (nowMs >= st.OverrideUntilMs
                || !devices.TryGetValue(st.OverrideHandle, out var cur)
                || !cur.IsConnected))
        {
            st.OverrideHandle = "";
        }
        // First sighting only primes the baseline; increases never trigger.
        // The freshest drop wins (lowest level breaks a same-tick tie); if
        // that is the shown device itself, it is already the news on screen.
        string? dropped = null;
        int droppedLevel = int.MaxValue;
        foreach (var d in devices.Values)
        {
            if (!d.IsConnected)
            {
                continue;
            }
            if (st.LastBattery.TryGetValue(d.Handle, out var prev)
                && d.BatteryPercentage < prev
                && d.BatteryPercentage < droppedLevel)
            {
                dropped = d.Handle;
                droppedLevel = d.BatteryPercentage;
            }
            st.LastBattery[d.Handle] = d.BatteryPercentage;
        }
        if (dropped is not null)
        {
            if (dropped == baseDevice?.Handle)
            {
                st.OverrideHandle = "";
            }
            else
            {
                st.OverrideHandle = dropped;
                st.OverrideUntilMs = nowMs + (long)swapSecs * 1000;
            }
        }
        return st.OverrideHandle.Length > 0 && devices.TryGetValue(st.OverrideHandle, out var swap)
            ? swap
            : baseDevice;
    }

    /// <summary>Carousel over connected devices ordered by name (stable, so
    /// the sequence is deterministic and testable).</summary>
    private static RazerDevice? PickRotate(
        IReadOnlyDictionary<string, RazerDevice> devices,
        ulong intervalSecs,
        DisplayModeState st,
        long nowMs)
    {
        var list = devices.Values
            .Where(d => d.IsConnected)
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToList();
        if (list.Count == 0)
        {
            st.RotateIndex = 0;
            st.LastRotateMs = 0;
            return null;
        }
        if (st.LastRotateMs == 0)
        {
            // First tick starts the clock; do not skip ahead an extra slot.
            st.LastRotateMs = nowMs;
        }
        else if (nowMs - st.LastRotateMs >= (long)intervalSecs * 1000)
        {
            st.RotateIndex++;
            st.LastRotateMs = nowMs;
        }
        // RotateIndex only increments or resets — never negative, so a
        // single modulo keeps it in range.
        st.RotateIndex %= list.Count;
        return list[st.RotateIndex];
    }
}
