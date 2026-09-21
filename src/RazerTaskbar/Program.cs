//! Process entry (port of src/main.rs's main). Everything of substance lives
//! in the core host (Host/AppHost.cs): the diagnostic probes, the
//! single-instance guard, the Windows App SDK bootstrap with its widget-only
//! degrade, the feature mounts and the exit policy.

using RazerTaskbar.Host;

namespace RazerTaskbar;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => AppHost.Run(args);
}
