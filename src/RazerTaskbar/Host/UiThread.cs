//! UI-thread marshalling for the host (Port of the Rust build's
//! `post_to_ui`): features run on the widget/watcher threads and reach the
//! XAML surface through here.

using Microsoft.UI.Dispatching;
using RazerTaskbar.Core;

namespace RazerTaskbar.Host;

public static class UiThread
{
    private static DispatcherQueue? _dispatcher;

    /// <summary>True once the XAML dispatcher exists (i.e. the control panel
    /// feature can actually create a window).</summary>
    public static bool Attached => _dispatcher is not null;

    /// <summary>Called by <see cref="App"/> once the XAML Application is up.</summary>
    public static void Attach(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    /// <summary>Run <paramref name="action"/> on the UI thread. Silently drops
    /// the call before the dispatcher exists (the widget menu stays usable
    /// in the XAML-less degraded mode) and never lets a UI exception escape
    /// into the dispatcher loop.</summary>
    public static void Post(Action action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null)
        {
            Log.Info("ui: dropped (no dispatcher)");
            return;
        }
        if (dispatcher.HasThreadAccess)
        {
            // Already there (a page's own handler, or the shutdown pass that
            // runs on the UI thread): run inline so the caller sees the
            // effect — a queued close would otherwise land after the loop has
            // already been asked to quit.
            Run(action);
            return;
        }
        if (!dispatcher.TryEnqueue(() => Run(action)))
        {
            Log.Info("ui: dropped (dispatcher refused)");
        }
    }

    private static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Log.Error("ui: action failed", e);
        }
    }
}
