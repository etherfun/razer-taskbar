//! Feature: the taskbar surface (docs/agent-architecture.md) — overlay /
//! embedded display window, tray icon, hover panel and the UIA layout
//! listener, all on one dedicated STA thread (port of src/window.rs's
//! run_message_loop).
//!
//! The thread is the feature's lifetime: the anchor window it creates owns
//! the timers, the tray callback and the TaskbarCreated broadcast, while the
//! display window itself is disposable (show_widget off/on, embed↔overlay
//! switches, explorer restarts). Nothing here ends the process — that stays
//! the host's call.

using RazerTaskbar.Core;
using RazerTaskbar.Host;
using RazerTaskbar.Native;

namespace RazerTaskbar.Features.Widget;

public sealed class WidgetFeature : AppFeature
{
    public override string Name => "widget";

    /// <summary>Required: this thread carries the tray icon too, so without it
    /// the app has no user surface left at all.</summary>
    public override bool Required => true;

    private Thread? _thread;

    public override bool Start()
    {
        _thread = new Thread(RunGuarded)
        {
            Name = "razer-widget",
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        return true;
    }

    /// <summary>Ask the widget thread to take the surface down (tray Exit,
    /// embed ghost frame, anchor WM_DESTROY). Posted, never called across
    /// threads: DestroyWindow belongs to the creating thread.</summary>
    public override void Stop() => AppState.PostExit();

    private void RunGuarded()
    {
        try
        {
            WidgetWindow.Run();
            Log.Info("feature widget: thread ended (message loop quit)");
        }
        catch (Exception e)
        {
            // The widget thread used to be able to kill the process outright
            // (.NET ends it on an unhandled thread exception); report to the
            // host instead, which decides what a dead required feature means.
            AppHost.OnFeatureFaulted(this, e);
        }
    }
}
