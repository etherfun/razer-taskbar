//! Core host (docs/agent-architecture.md): process boot, the service
//! singletons shared by every feature (config / history / i18n) and the
//! feature registry — plus the single place that decides when the process
//! ends.
//!
//! Lifetime rule this file exists for: **no feature owns the process**. The
//! settings/history panel can be destroyed and rebuilt on demand, the taskbar
//! widget can be switched off and back on, and only an explicit exit (tray
//! menu / widget WM_CLOSE / a required feature failing to mount) ends the
//! app. Before this split the only XAML window had to be hidden instead of
//! closed, because destroying it returned from `Application.Start` and took
//! every other thread with it.

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.DynamicDependency;
using RazerTaskbar.Core;
using RazerTaskbar.Features.ControlPanel;
using RazerTaskbar.Features.Data;
using RazerTaskbar.Features.Widget;
using RazerTaskbar.Native;

namespace RazerTaskbar.Host;

public static class AppHost
{
    private static readonly FeatureRegistry Features = new();
    private static readonly KeepAliveWindow KeepAlive = new();
    /// <summary>Held open in the XAML-less degraded mode, where Main waits on
    /// it instead of returning (and exiting) under the widget thread.</summary>
    private static readonly ManualResetEventSlim ExitGate = new(false);

    private static ControlPanelFeature? _controlPanel;
    private static Application? _app;
    private static volatile bool _xamlAvailable;
    private static volatile bool _uiReady;
    private static int _exitRequested;

    /// <summary>False when the Windows App SDK runtime is missing: the tray
    /// menu entries for the settings/history panel are grayed (widget-only
    /// degraded mode).</summary>
    public static bool XamlAvailable => _xamlAvailable;

    /// <summary>Port of src/main.rs's main(): probe → single instance →
    /// bootstrap → core services → mount features → run the UI loop.</summary>
    public static int Run(string[] args)
    {
        Log.Install();

        // Standalone diagnostics (--hid-probe / --hid-scan / --hid-power /
        // --hid-tx / --hid-deep / --ble-vendor) run before the single-instance
        // guard: the app may be running concurrently and these never touch the UI.
        if (args.Contains("--hid-probe") || args.Contains("--hid-scan") || args.Contains("--hid-power")
            || args.Contains("--hid-tx") || args.Contains("--hid-deep"))
        {
            return HidProbe.Run(args);
        }
        if (args.Contains("--ble-vendor"))
        {
            return BleVendorProbe.Run(args);
        }

        // Single instance (port of find_existing_instance): the overlay is a
        // findable top-level window; embedded children need the enum fallback.
        if (SingleInstance.AnotherInstanceRunning())
        {
            Log.Info("another instance running, exiting");
            return 0;
        }

        // Unpackaged apps must bind to the installed Windows App SDK runtime
        // (same call shape as the package's MddBootstrapAutoInitializer).
        // Missing runtime => widget-only degraded mode, no XAML features.
        // Options.None keeps the failure silent; we degrade ourselves.
        uint majorMinorVersion = Microsoft.WindowsAppSDK.Release.MajorMinor;
        string versionTag = Microsoft.WindowsAppSDK.Release.VersionTag;
        var minVersion = new PackageVersion(Microsoft.WindowsAppSDK.Runtime.Version.UInt64);
        _xamlAvailable = Bootstrap.TryInitialize(
            majorMinorVersion, versionTag, minVersion, Bootstrap.InitializeOptions.None, out _);
        Log.Info($"bootstrap ok={_xamlAvailable}");

        // Core services: everything the features share.
        I18n.Init();
        HistoryService.Init();
        AppState.Instance.LoadInitialConfig();

        // Mount features in dependency order. The watcher feeds the device
        // roster the widget paints; the control panel mounts no work (its
        // window is born on the first open and destroyed on close).
        _controlPanel = new ControlPanelFeature();
        Features.Mount(new WatcherFeature());
        Features.Mount(new WidgetFeature());
        Features.Mount(_controlPanel);

        if (_xamlAvailable)
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ => new App());
        }
        else
        {
            // No dispatcher to own the process lifetime, so the widget thread's
            // message loop does: wait here rather than returning, which would
            // exit the process while the widget is still on screen.
            Log.Info("core: xaml unavailable, widget-only until exit");
            ExitGate.Wait();
        }

        Shutdown("main-return");
        return 0;
    }

    /// <summary>Called from the XAML <see cref="App"/> constructor, once the
    /// dispatcher exists. Creates the window that keeps the loop alive.</summary>
    public static void OnXamlAppReady(Application app, DispatcherQueue dispatcher)
    {
        _app = app;
        UiThread.Attach(dispatcher);
        _uiReady = true;
        KeepAlive.EnsureCreated();
        UiSelftest.Schedule();
    }

    /// <summary>Open (or focus) the single settings/history window. Safe from
    /// any thread; no-op in the degraded mode.</summary>
    public static void ShowControlPanel(ControlPanelPage page)
    {
        var panel = _controlPanel;
        if (panel is null || !_xamlAvailable)
        {
            return;
        }
        panel.Show(page);
    }

    /// <summary>Close the panel (hide; instant reopen, no tree recreation).
    /// The window stays owned by the feature, not by the process.</summary>
    public static void CloseControlPanel() => _controlPanel?.Close();

    /// <summary>Destroy the panel window (exit path; the harness's destroy
    /// test). The next <see cref="ShowControlPanel"/> builds a fresh one.</summary>
    public static void DestroyControlPanel() => _controlPanel?.Destroy();

    /// <summary>HWND of the panel window, if one is open (file pickers need an
    /// owner window on desktop apps). UI thread only — the page that asks for
    /// it lives in that window.</summary>
    public static bool TryGetControlPanelHandle(out IntPtr hwnd)
    {
        hwnd = 0;
        var panel = _controlPanel;
        return panel is not null && panel.TryGetWindowHandle(out hwnd);
    }

    /// <summary>A feature's own thread failed after mounting. Optional
    /// features keep the app running; a required one (the widget: the entire
    /// user surface, tray included) leaves nothing usable behind, so it ends
    /// the process — after logging the cause.</summary>
    internal static void OnFeatureFaulted(IAppFeature feature, Exception e)
    {
        Features.Fault(feature, e);
        if (feature.Required)
        {
            Log.Error($"core: required feature {feature.Name} is down, ending");
            RequestExit($"fault:{feature.Name}");
        }
    }

    /// <summary>End the app (tray Exit / widget WM_CLOSE / mount failure).
    /// Callable from any thread; the first caller wins, later ones are
    /// ignored so a re-entrant teardown cannot double-unmount features.</summary>
    public static void RequestExit(string reason)
    {
        if (Interlocked.Exchange(ref _exitRequested, 1) != 0)
        {
            return;
        }
        Log.Info($"core: exit requested ({reason})");

        if (!_uiReady)
        {
            Shutdown(reason);
            if (_xamlAvailable)
            {
                // A feature failed before App was constructed: Application.Start
                // is about to run with nothing left to drive it, so end the
                // process here (same startup race the Rust build resolved by
                // exiting).
                Environment.Exit(0);
            }
            return; // degraded mode: Main returns off ExitGate
        }

        UiThread.Post(() =>
        {
            Shutdown(reason);
            KeepAlive.Destroy();
            _app?.Exit();
        });
    }

    /// <summary>Bytes of managed heap + private memory, for the self-test log
    /// (docs/agent-build.md memory method).</summary>
    internal static string MemoryStamp()
    {
        long heap = GC.GetTotalMemory(false);
        long priv = 0;
        try
        {
            priv = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
        }
        catch (Exception)
        {
            // best effort — the stamp is diagnostic only
        }
        return $"heap={heap / 1024} KiB private={priv / (1024 * 1024)} MiB";
    }

    /// <summary>Orderly teardown: unmount features newest-first, then close
    /// the core-owned services. Idempotent.</summary>
    private static void Shutdown(string reason)
    {
        Features.UnmountAll(reason);
        HistoryService.Close();
        ExitGate.Set();
    }
}
