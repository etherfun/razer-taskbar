//! Port of window.rs STATE + the settings-view surface (modify_config /
//! set_shown_device / reposition_widget). The config copy here is the single
//! authoritative value on the widget thread; the WinUI settings page lives on
//! the main thread and edits flow back through WidgetThread.Post closures.
//! Consumers without a widget-thread presence (watcher, history) re-read
//! settings.json on their own cadence.

using System.Runtime.InteropServices;
using RazerTaskbar.Core;
using static RazerTaskbar.Native.Interop.Win32Consts;

namespace RazerTaskbar.Native;

/// <summary>Closure marshaler for the widget (STA message-loop) thread:
/// WM_APP_INVOKE carries a GCHandle-wrapped Action. Used by both the WinUI
/// thread (settings edits) and cross-module calls.</summary>
public static class WidgetThread
{
    /// <summary>WM_APP+3: the next free WM_APP slot after tray (+1) and layout (+2).</summary>
    public const uint WmInvoke = WM_APP + 3;

    private static IntPtr _hwnd;

    public static void Init(IntPtr hwnd) => _hwnd = hwnd;

    public static IntPtr Hwnd => _hwnd;

    public static void Post(Action action)
    {
        var hwnd = _hwnd;
        if (hwnd == 0)
        {
            return; // widget not up yet (nothing to marshal to)
        }
        var handle = GCHandle.Alloc(action);
        if (!Interop.User32.PostMessageW(hwnd, WmInvoke, 0, (IntPtr)handle))
        {
            handle.Free();
        }
    }

    /// <summary>WndProc hook: runs the posted closure and frees its handle.</summary>
    public static void HandleInvoke(IntPtr lParam)
    {
        var handle = (GCHandle)lParam;
        var action = (Action?)handle.Target;
        handle.Free();
        action?.Invoke();
    }
}

public sealed class AppState
{
    public static readonly AppState Instance = new();

    private readonly object _configLock = new();
    private Config _config = new();

    public DeviceStore Devices { get; } = new();

    /// <summary>Display-mode runtime state (drop-swap override / rotate
    /// cursor). Widget-thread only; timestamp-idempotent per tick.</summary>
    public DisplayModeState ModeState { get; } = new();

    private AppState() { }

    /// <summary>Thread-safe config snapshot (UI thread init/refresh use).</summary>
    public Config ConfigSnapshot()
    {
        lock (_configLock)
        {
            return _config.Clone();
        }
    }

    /// <summary>Load the authoritative config copy at startup (widget thread).</summary>
    public void LoadInitialConfig()
    {
        lock (_configLock)
        {
            _config = ConfigService.Load();
        }
    }

    /// <summary>Apply a config mutation to the authoritative copy and persist
    /// it. Widget thread only (port of window.rs modify_config).</summary>
    public void ModifyConfigOnWidgetThread(Action<Config> f)
    {
        lock (_configLock)
        {
            f(_config);
            ConfigService.Save(_config);
        }
    }

    // — Widget-thread helpers (window.rs pub surface) —

    /// <summary>Re-run the placement pass and repaint (edits that change
    /// position or width: side, avoid-overlap, estimated-time toggle).</summary>
    public static void RepositionWidget()
    {
        WidgetWindow.PlaceWidget();
        WidgetWindow.InvalidateDisplay();
    }

    public static void InvalidateWidget() => WidgetWindow.InvalidateDisplay();

    /// <summary>Switch the displayed device ("" = auto). Stamps `is_selected`
    /// so the pick rule follows immediately (the watcher re-reads the config
    /// each cycle regardless).</summary>
    public void SetShownDevice(string handle)
    {
        lock (_configLock)
        {
            _config.ShownDeviceHandle = handle;
            ConfigService.Save(_config);
        }
        var shown = handle;
        Devices.Mutate(devices =>
        {
            foreach (var key in devices.Keys.ToArray())
            {
                var d = devices[key];
                devices[key] = d with { IsSelected = shown.Length == 0 || d.Handle == shown };
            }
        });
        InvalidateWidget();
    }

    // — UI-thread entry points (marshal onto the widget thread) —

    public static void PostModifyConfig(Action<Config> edit) => WidgetThread.Post(() => Instance.ModifyConfigOnWidgetThread(edit));

    public static void PostSetShownDevice(string handle) => WidgetThread.Post(() => Instance.SetShownDevice(handle));

    /// <summary>Mode or mode-tuning changed: start the new strategy from a
    /// clean slate (no stale override, rotation restarts at slot 0, battery
    /// baselines re-prime without spurious drop triggers).</summary>
    public static void PostResetModeState() => WidgetThread.Post(() => Instance.ModeState.Reset());

    /// <summary>Transition toggle turned off mid-animation: stop blending on
    /// the widget thread, keeping the currently presented frame.</summary>
    public static void PostResetFade() => WidgetThread.Post(WidgetWindow.CancelFade);

    public static void PostReposition() => WidgetThread.Post(RepositionWidget);

    public static void PostTraySetEnabled(bool enabled) => WidgetThread.Post(() => TrayIcon.SetEnabled(enabled));

    /// <summary>show_widget toggle: create/destroy the taskbar display window
    /// while the widget thread (tray, config marshaling) stays up.</summary>
    public static void PostWidgetSetEnabled(bool enabled) => WidgetThread.Post(() => WidgetWindow.SetWidgetEnabled(enabled));

    public static void PostHoverHide() => WidgetThread.Post(HoverPanel.Hide);

    public static void PostTrayRefresh() => WidgetThread.Post(TrayIcon.Refresh);

    public static void PostExit() => WidgetThread.Post(WidgetWindow.ExitWidget);
}

public static class ConfigExt
{
    /// <summary>Shallow member copy: all fields are value types or immutable strings.</summary>
    public static Config Clone(this Config c) => new()
    {
        RunAtStartup = c.RunAtStartup,
        PollingThrottleSecs = c.PollingThrottleSecs,
        ShownDeviceHandle = c.ShownDeviceHandle,
        DisplayMode = c.DisplayMode,
        SwapDisplaySecs = c.SwapDisplaySecs,
        RotateIntervalSecs = c.RotateIntervalSecs,
        SynapseVersion = c.SynapseVersion,
        BatterySource = c.BatterySource,
        WidgetSide = c.WidgetSide,
        EmbedIntoWidgetsSpace = c.EmbedIntoWidgetsSpace,
        AvoidOverlapWithWidgets = c.AvoidOverlapWithWidgets,
        TaskbarLeftSpaceWin11 = c.TaskbarLeftSpaceWin11,
        TaskbarRightSpaceWin11 = c.TaskbarRightSpaceWin11,
        WindowOffsetLeft = c.WindowOffsetLeft,
        WindowOffsetTop = c.WindowOffsetTop,
        ShowWidget = c.ShowWidget,
        ShowTrayIcon = c.ShowTrayIcon,
        HoverDevices = c.HoverDevices,
        RecordBatteryHistory = c.RecordBatteryHistory,
        ShowEstimatedTime = c.ShowEstimatedTime,
        ColorBatteryIcon = c.ColorBatteryIcon,
        FadeTransition = c.FadeTransition,
        HistoryPollIntervalSecs = c.HistoryPollIntervalSecs,
        Language = c.Language,
        EmbedIntoTaskbar = c.EmbedIntoTaskbar,
    };
}
