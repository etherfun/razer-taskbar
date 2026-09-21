//! Headless UI harness (env-var driven, off by default): drives the control
//! panel through its lifecycle without a human at the keyboard, so the
//! feature-lifetime contract can be verified from the log file alone.
//! Documented in docs/agent-build.md ("运行与调试").
//!
//!   RAZER_TASKBAR_OPEN_WINDOW_SECS=N          open the settings panel once after N s
//!   RAZER_TASKBAR_PANEL_CYCLE_SECS=N          after N s: open → close (hide) →
//!                                             reopen → close, stamping heap and
//!                                             private bytes
//!   RAZER_TASKBAR_PANEL_CYCLE_REPEAT=N        repeat that cycle N times (leak
//!                                             regression: memory/handles must
//!                                             stay flat)
//!   RAZER_TASKBAR_PANEL_DESTROY_CYCLE_SECS=N  after N s: open → DESTROY →
//!                                             reopen (the structural guarantee:
//!                                             no window owns the process)
//!   RAZER_TASKBAR_EXIT_AFTER_SECS=N           after N s: take the widget down
//!                                             the way the tray Exit item does

using RazerTaskbar.Core;
using RazerTaskbar.Features.ControlPanel;
using RazerTaskbar.Native;

namespace RazerTaskbar.Host;

internal static class UiSelftest
{
    private const int StepMs = 5000;

    public static void Schedule()
    {
        int openSecs = ReadSeconds("RAZER_TASKBAR_OPEN_WINDOW_SECS");
        int cycleSecs = ReadSeconds("RAZER_TASKBAR_PANEL_CYCLE_SECS");
        int cycleRepeat = ReadSeconds("RAZER_TASKBAR_PANEL_CYCLE_REPEAT");
        int destroySecs = ReadSeconds("RAZER_TASKBAR_PANEL_DESTROY_CYCLE_SECS");
        int exitSecs = ReadSeconds("RAZER_TASKBAR_EXIT_AFTER_SECS");
        if (openSecs <= 0 && cycleSecs <= 0 && destroySecs <= 0 && exitSecs <= 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (openSecs > 0)
                {
                    await Task.Delay(openSecs * 1000);
                    Log.Info("selftest: opening settings panel");
                    AppHost.ShowControlPanel(ControlPanelPage.Settings);
                }
                if (cycleSecs > 0)
                {
                    await Task.Delay(cycleSecs * 1000);
                    await HideCycleAsync(cycleRepeat < 1 ? 1 : cycleRepeat);
                }
                if (destroySecs > 0)
                {
                    await Task.Delay(destroySecs * 1000);
                    await DestroyCycleAsync();
                }
                if (exitSecs > 0)
                {
                    await Task.Delay(exitSecs * 1000);
                    Log.Info("selftest: requesting exit (tray Exit path)");
                    AppState.PostExit();
                }
            }
            catch (Exception e)
            {
                Log.Error("selftest failed", e);
            }
        });
    }

    /// <summary>The everyday path: closing the panel hides it, so no XAML tree
    /// is rebuilt and memory must stay flat across cycles.</summary>
    private static async Task HideCycleAsync(int repeat)
    {
        Log.Info($"selftest: open panel ({AppHost.MemoryStamp()})");
        AppHost.ShowControlPanel(ControlPanelPage.History);
        await Task.Delay(StepMs);
        AppHost.CloseControlPanel();
        await Task.Delay(1500);
        Log.Info($"selftest: panel hidden #1 ({AppHost.MemoryStamp()})");

        // While closed, flip the tab as well: the pages are cached
        // (NavigationCacheMode.Required), so this must not build new trees.
        for (int i = 0; i < repeat; i++)
        {
            AppHost.ShowControlPanel(ControlPanelPage.Settings);
            await Task.Delay(1500);
            AppHost.CloseControlPanel();
            await Task.Delay(1500);
            Log.Info($"selftest: hide cycle #{i + 1} ({AppHost.MemoryStamp()})");
        }
        Log.Info("selftest: done, still alive");
    }

    /// <summary>The structural guarantee: destroying the window must leave the
    /// process (widget thread, watcher, tray) running, and the panel must come
    /// back afterwards.</summary>
    private static async Task DestroyCycleAsync()
    {
        Log.Info($"selftest: open panel ({AppHost.MemoryStamp()})");
        AppHost.ShowControlPanel(ControlPanelPage.History);
        await Task.Delay(StepMs);

        Log.Info("selftest: destroy panel (not hide)");
        AppHost.DestroyControlPanel();
        await Task.Delay(StepMs);

        Log.Info($"selftest: alive after destroy ({AppHost.MemoryStamp()})");
        AppHost.ShowControlPanel(ControlPanelPage.Settings);
        await Task.Delay(StepMs);

        Log.Info($"selftest: panel rebuilt ({AppHost.MemoryStamp()})");
        AppHost.DestroyControlPanel();
        await Task.Delay(StepMs);
        Log.Info($"selftest: done, still alive ({AppHost.MemoryStamp()})");
    }

    private static int ReadSeconds(string name)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var secs) && secs > 0 ? secs : 0;
}
