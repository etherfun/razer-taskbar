//! XAML application shell (port of the window-opening parts of
//! src/window.rs). It creates no window of its own any more: the host mounts
//! the features (Host/AppHost.cs) and the control-panel feature owns the
//! settings/history window. This class exists to receive the framework's
//! Application object, install the unhandled-exception hook and hand the
//! dispatcher to the host.

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using RazerTaskbar.Host;

namespace RazerTaskbar;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        // Keep the widget alive on UI failures (Rust semantics: log, don't die).
        LogXaml.Install(this);
        AppHost.OnXamlAppReady(this, DispatcherQueue.GetForCurrentThread());
    }
}
