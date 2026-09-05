//! XAML unhandled-exception hook: keep the widget alive on UI failures
//! (Rust semantics: log, don't die). Lives in the App project because it
//! touches Microsoft.UI.Xaml.

using Microsoft.UI.Xaml;
using RazerTaskbar.Core;

namespace RazerTaskbar;

public static class LogXaml
{
    public static void Install(Application app)
    {
        app.UnhandledException += (_, e) =>
        {
            Log.Error("Application.UnhandledException", e.Exception);
            e.Handled = true;
        };
    }
}
