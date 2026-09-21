//! Feature contract for the core host (docs/agent-architecture.md).
//!
//! Everything the app shows or polls mounts as one feature, so each surface
//! owns its own lifetime: the taskbar widget can be taken down (show_widget
//! off, widget thread failure), the settings/history panel can be opened and
//! destroyed, and neither ends the process. Only the host decides that.

namespace RazerTaskbar.Host;

public enum FeatureState
{
    /// <summary>Registered but not running (or already stopped).</summary>
    Stopped,
    Mounted,
    /// <summary>Mount failed or its supervisory thread died: the feature is
    /// out of commission, the rest of the app keeps running.</summary>
    Faulted,
}

public interface IAppFeature
{
    /// <summary>Stable id used in logs (also the doc/table name of the feature).</summary>
    string Name { get; }

    /// <summary>True when the app cannot work without this feature: a mount
    /// failure ends the process (the taskbar widget is the entire user
    /// surface). Optional features degrade instead.</summary>
    bool Required { get; }

    /// <summary>Written only by the host's <see cref="FeatureRegistry"/>.</summary>
    FeatureState State { get; set; }

    /// <summary>Mount the feature (start threads, claim resources). Runs on
    /// the calling thread — the host mounts on the main thread before the
    /// XAML loop starts. Returns false when the feature could not mount.</summary>
    bool Start();

    /// <summary>Unmount: release windows/threads. Must be safe to call twice
    /// and safe to call when <see cref="Start"/> failed.</summary>
    void Stop();
}

/// <summary>Convenience base: the registry owns <see cref="State"/>, features
/// only implement the work.</summary>
public abstract class AppFeature : IAppFeature
{
    public abstract string Name { get; }

    public virtual bool Required => false;

    public FeatureState State { get; set; } = FeatureState.Stopped;

    public abstract bool Start();

    public abstract void Stop();
}
