//! Feature: the settings/history panel (docs/agent-architecture.md).
//!
//! One lazily created NavigationView window (WinUI3 merge of viewer.rs +
//! settings.rs), owned by this feature — never by the process: the host's
//! keep-alive window holds the dispatcher, so this surface can come and go
//! without ending the app.
//!
//! Close means HIDE, not destroy. Measured reasons (2026-09-21, see
//! docs/agent-architecture.md "窗口重建会泄漏原生内存"):
//!
//!   1. Recreating a XAML tree leaks native memory + handles in WinUI3: an
//!      open→close cycle cost ~6 MB / ~30 handles even after the content
//!      teardown below and a forced collect (~15-25 MB without it), linearly
//!      over 8 cycles, while the managed heap came back to 1.9 MB every time.
//!   2. Hiding is instant; rebuilding also re-runs the page reload.
//!
//! `Destroy()` still exists and is exercised by the harness
//! (`RAZER_TASKBAR_PANEL_DESTROY_CYCLE_SECS`) and by `Stop()`. The point of
//! the core/feature split is that destroying this window is *possible* at any
//! moment — not that the close button has to do it.

using Microsoft.UI.Xaml;
using RazerTaskbar.Core;
using RazerTaskbar.Host;

namespace RazerTaskbar.Features.ControlPanel;

public enum ControlPanelPage
{
    History,
    Settings,
}

public sealed class ControlPanelFeature : AppFeature
{
    public override string Name => "control-panel";

    /// <summary>UI thread only: the window, alive from the first open until it
    /// is destroyed (hidden while closed).</summary>
    private ControlPanelWindow? _window;

    /// <summary>UI thread only: set while <see cref="DestroyOnUiThread"/> is
    /// closing the window, so the Closing hook lets it through instead of
    /// turning the close into a hide.</summary>
    private bool _destroying;

    public override bool Start()
    {
        // Only the language switch needs a live window; the subscription is
        // process-lifetime, matching the feature's own lifetime.
        I18n.LanguageChanged += OnLanguageChanged;
        return true;
    }

    public override void Stop()
    {
        I18n.LanguageChanged -= OnLanguageChanged;
        Destroy();
    }

    /// <summary>Open the panel on a tab, creating the window on first use.
    /// Any thread.</summary>
    public void Show(ControlPanelPage page) => UiThread.Post(() => ShowOnUiThread(page));

    /// <summary>Close the panel (hide): window and page trees stay resident,
    /// the next Show is instant. Any thread.</summary>
    public void Close() => UiThread.Post(HideOnUiThread);

    /// <summary>Tear the window down for real (app exit; the harness's
    /// destroy-path test). Any thread; the next Show rebuilds from scratch.</summary>
    public void Destroy() => UiThread.Post(DestroyOnUiThread);

    /// <summary>HWND of the panel window, if one exists (file pickers need an
    /// owner window on desktop apps). UI thread only — every caller lives
    /// inside that window.</summary>
    public bool TryGetWindowHandle(out IntPtr hwnd)
    {
        hwnd = 0;
        var window = _window;
        if (window is null)
        {
            return false;
        }
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        return hwnd != 0;
    }

    private void ShowOnUiThread(ControlPanelPage page)
    {
        if (_window is null)
        {
            Log.Info("control-panel: creating window (first open since last destroy)");
            var created = new ControlPanelWindow();
            created.Closed += OnWindowClosed;
            // User close (X / Alt+F4) hides instead of destroying: destroying
            // and rebuilding the tree is what leaks (see the class comment).
            created.AppWindow.Closing += OnWindowClosing;
            _window = created;
        }
        _window.OpenPage(page);
        // A hidden window must be shown again before Activate (close = hide,
        // see HideOnUiThread).
        _window.AppWindow.Show();
        _window.Activate();
    }

    private void HideOnUiThread()
    {
        var window = _window;
        if (window is null)
        {
            return;
        }
        // Hides the window but keeps its HWND, content island and page trees
        // resident — which is the whole point: no recreation, no leak, and an
        // instant reopen.
        window.AppWindow.Hide();
        Log.Info("control-panel: window hidden");
    }

    /// <summary>The close button / Alt+F4: hide the surface (tray-app
    /// behaviour, and the only thing that does not leak). <see
    /// cref="DestroyOnUiThread"/> sets <see cref="_destroying"/> to get the
    /// real close through.</summary>
    private void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_destroying)
        {
            return;
        }
        args.Cancel = true;
        HideOnUiThread();
    }

    private void DestroyOnUiThread()
    {
        var window = _window;
        if (window is null)
        {
            return;
        }
        // Drop the heavy parts explicitly before the HWND goes away: the page
        // trees hang off Content and the backdrop owns a DWM system backdrop.
        // This is what makes the destroy path as cheap as WinUI3 allows —
        // without it the same test measured ~15-25 MB per cycle instead of
        // ~6 MB (docs/agent-architecture.md).
        window.Content = null;
        window.SystemBackdrop = null;
        _window = null; // the Closed handler stays a no-op for this window
        _destroying = true;
        try
        {
            window.Close();
        }
        finally
        {
            _destroying = false;
        }
        // The page trees are WinRT objects reached through managed wrappers:
        // collect now so the native peers are released promptly. Forced (not
        // Optimized — a hint the GC is free to ignore, which is how the leak
        // was first measured), non-compacting, so the UI thread is not blocked
        // by a compaction.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
    }

    /// <summary>Framework-initiated close (Alt+F4, or a destroy from outside
    /// this feature): the window is gone.</summary>
    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (ReferenceEquals(_window, sender))
        {
            _window = null;
        }
        Log.Info($"control-panel: window destroyed ({AppHost.MemoryStamp()})");
    }

    private void OnLanguageChanged() => UiThread.Post(() => _window?.SyncLanguage());
}
