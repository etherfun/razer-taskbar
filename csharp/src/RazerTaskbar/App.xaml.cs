//! Port of the window-opening parts of src/window.rs: settings/history are
//! single-instance windows on the UI thread; opening from the tray menu on
//! the widget thread is marshaled onto the UI DispatcherQueue.

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using RazerTaskbar.Core;
using RazerTaskbar.Native;

namespace RazerTaskbar;

public partial class App : Application
{
    public static App Instance { get; private set; } = null!;

    /// <summary>False when the Windows App SDK runtime is missing: tray menu
    /// entries for settings/history are grayed (widget-only degraded mode).</summary>
    public static bool XamlAvailable { get; set; } = true;

    private readonly DispatcherQueue _dispatcher;
    private MainWindow? _mainWindow;

    public App()
    {
        Instance = this;
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // Language switch from the settings page: re-localize open UI surfaces
        // (the tray refresh is marshaled to the widget thread separately).
        I18n.LanguageChanged += SyncMainWindowLanguage;
        I18n.LanguageChanged += AppState.PostTrayRefresh;
    }

    /// <summary>Open (or focus) the single NavigationView window. Safe to call
    /// from any thread; the actual work is marshaled to the UI thread. The
    /// window is built lazily the first time (idle memory stays low).</summary>
    public static void ShowMainWindow(bool selectSettings = false)
    {
        if (!XamlAvailable)
        {
            return;
        }
        Instance._dispatcher.TryEnqueue(() =>
        {
            if (Instance._mainWindow is null)
            {
                Instance._mainWindow = new MainWindow();
            }
            Instance._mainWindow.OpenTab(selectSettings);
            Instance._mainWindow.Activate();
        });
    }

    /// <summary>Re-localize the (already created) window after a language switch.</summary>
    public static void SyncMainWindowLanguage()
    {
        Instance._dispatcher.TryEnqueue(() => Instance._mainWindow?.SyncLanguage());
    }

    /// <summary>End the app (widget Exit / widget thread failure). Must be
    /// called on the UI thread; marshals itself when needed.</summary>
    public static void RequestExit()
    {
        var app = Instance;
        if (app is null)
        {
            // Widget failed before Application.Start constructed the App
            // (startup race): end the process directly.
            Environment.Exit(0);
            return;
        }
        app._dispatcher.TryEnqueue(() => Current.Exit());
    }
}
