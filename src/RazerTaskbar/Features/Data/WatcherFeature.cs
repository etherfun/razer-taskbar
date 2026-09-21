//! Feature: data acquisition (docs/agent-watcher.md) — Synapse log parsing,
//! HID/BLE direct reads and history sampling, on the dedicated razer-watcher
//! thread. Owns no UI, so it can be unmounted on its own; the host mounts it
//! first because the widget's first paint needs the roster it publishes.

using RazerTaskbar.Core;
using RazerTaskbar.Host;
using RazerTaskbar.Native;

namespace RazerTaskbar.Features.Data;

public sealed class WatcherFeature : AppFeature
{
    public override string Name => "watcher";

    private RazerWatcher? _watcher;
    private Thread? _thread;

    public override bool Start()
    {
        int pollSecs = (int)Math.Max(AppState.Instance.ConfigSnapshot().PollingThrottleSecs, 2);
        _watcher = new RazerWatcher(AppState.Instance.Devices);
        var watcher = _watcher;
        _thread = new Thread(() => watcher.Run(pollSecs))
        {
            Name = "razer-watcher",
            IsBackground = true,
        };
        _thread.Start();
        return true;
    }

    /// <summary>Cooperative stop + a bounded join: the tick loop exits on the
    /// wake the stop request releases, so the join normally returns at once.
    /// The thread is a background thread either way — a slow pass (a whole-log
    /// read) must not hold the exit up.</summary>
    public override void Stop()
    {
        _watcher?.RequestStop();
        var thread = _thread;
        if (thread is null || thread == Thread.CurrentThread || !thread.IsAlive)
        {
            return;
        }
        if (!thread.Join(TimeSpan.FromSeconds(2)))
        {
            Log.Info("feature watcher: thread still finishing a pass, exiting anyway");
        }
    }
}
