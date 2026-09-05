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
        // Single instance (port of find_existing_instance): the overlay is a
        // findable top-level window; embedded children need the enum fallback.
        if (SingleInstance.AnotherInstanceRunning())
        {
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
