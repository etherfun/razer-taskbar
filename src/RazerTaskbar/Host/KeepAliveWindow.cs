//! Hidden XAML window that owns the dispatcher loop's lifetime.
//!
//! WinUI3 ends the app when the last XAML window is destroyed: `Application.
//! Start`'s loop returns and Main would exit, taking the widget/watcher
//! threads with it (docs/agent-architecture.md, "为什么需要保活窗口"). The
//! settings/history panel is now a disposable feature — closed = destroyed,
//! with its ~98 MB XAML page tree handed back to the GC — so the loop needs a
//! window that never closes. This is it: created with the Application, never
//! activated, never shown, never destroyed except in an orderly shutdown.

using Microsoft.UI.Xaml;
using RazerTaskbar.Core;

namespace RazerTaskbar.Host;

internal sealed class KeepAliveWindow
{
    private Window? _window;
    private bool _allowClose;

    public void EnsureCreated()
    {
        if (_window is not null)
        {
            return;
        }
        var window = new Window { Title = "RazerTaskbarKeepAlive" };
        // The host is the only thing that may close it (shutdown path).
        window.AppWindow.Closing += (_, args) => args.Cancel = !_allowClose;
        _window = window;
        Log.Info("core: ui keep-alive window created (dispatcher anchor)");
    }

    /// <summary>Close it during shutdown. Only meaningful on the UI thread;
    /// the process is ending anyway, so a detached call just leaks.</summary>
    public void Destroy()
    {
        var window = _window;
        if (window is null)
        {
            return;
        }
        _allowClose = true;
        _window = null;
        window.Close();
    }
}
