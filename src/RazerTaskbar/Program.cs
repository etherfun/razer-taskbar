//! Port of src/main.rs: process entry, single-instance guard, Windows App SDK
//! bootstrap with graceful degrade to widget-only mode, thread startup
//! (widget STA thread + watcher thread; UIA thread spawns from the widget).

using Microsoft.Windows.ApplicationModel.DynamicDependency;
using Microsoft.UI.Xaml;
using RazerTaskbar.Core;
using RazerTaskbar.Native;

namespace RazerTaskbar;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Log.Install();

        // Standalone diagnostic (--hid-probe): enumerate Razer HID devices
        // and exercise the direct battery/charging queries. Runs before the
        // single-instance guard (the app may be running concurrently) and
        // before any UI bootstrap.
        if (args.Contains("--hid-probe") || args.Contains("--hid-scan"))
        {
            return HidProbe.Run(args);
        }

        // Standalone BLE vendor-channel probe (--ble-vendor): replay/sweep the
        // Razer private GATT command service. Needs Synapse stopped (it holds
        // the channel); read-only unless explicitly overridden.
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
        App.XamlAvailable = Bootstrap.TryInitialize(majorMinorVersion, versionTag, minVersion, Bootstrap.InitializeOptions.None, out _);
        Log.Info($"bootstrap ok={App.XamlAvailable}");

        // Startup order mirrors main.rs: config → history → i18n → threads.
        I18n.Init();
        HistoryService.Init();
        AppState.Instance.LoadInitialConfig();

        // Watcher thread ("razer-watcher"): log parsing + history sampling.
        var watcher = new RazerWatcher(AppState.Instance.Devices);
        int pollSecs = (int)Math.Max(AppState.Instance.ConfigSnapshot().PollingThrottleSecs, 2);
        var watcherThread = new Thread(() => watcher.Run(pollSecs))
        {
            Name = "razer-watcher",
            IsBackground = true,
        };
        watcherThread.Start();

        // Widget thread (STA, owns the message loop): overlay + hover + tray.
        var widgetThread = new Thread(WidgetWindow.Run)
        {
            Name = "razer-widget",
            IsBackground = true,
        };
        widgetThread.SetApartmentState(ApartmentState.STA);
        widgetThread.Start();

        // Test harness (headless verification of the XAML path): open the
        // settings window after N seconds when this env var is set.
        if (int.TryParse(Environment.GetEnvironmentVariable("RAZER_TASKBAR_OPEN_WINDOW_SECS"), out var openSecs) && openSecs > 0)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(openSecs * 1000);
                Log.Info("test harness: opening settings window");
                App.ShowMainWindow(selectSettings: true);
            });
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var app = new App();
            // No window is created here: MainWindow is built lazily the first
            // time the tray menu asks for it, keeping idle memory low.
        });

        // Application.Start returns when the app exits (widget Exit →
        // WM_DESTROY → App.RequestExit). Background threads die with the
        // process; the Rust build relies on the same main-returns behavior.
        HistoryService.Close();
        return 0;
    }
}
