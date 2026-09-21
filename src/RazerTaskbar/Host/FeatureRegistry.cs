//! The host's feature table: mount in order, unmount in reverse, and report
//! faults without taking the process down (docs/agent-architecture.md).

using RazerTaskbar.Core;

namespace RazerTaskbar.Host;

public sealed class FeatureRegistry
{
    private readonly List<IAppFeature> _features = new();
    private readonly object _gate = new();
    private bool _unmounted;

    public IReadOnlyList<IAppFeature> Mounted
    {
        get
        {
            lock (_gate)
            {
                return _features.ToArray();
            }
        }
    }

    /// <summary>Start one feature and record its state. A failed optional
    /// feature stays registered (its later Start/Stop calls are no-ops) so the
    /// registry still mirrors what the app is made of.</summary>
    public bool Mount(IAppFeature feature)
    {
        lock (_gate)
        {
            if (_unmounted)
            {
                Log.Info($"feature {feature.Name}: not mounted (host is shutting down)");
                return false;
            }
            if (_features.Any(f => f.Name == feature.Name))
            {
                Log.Error($"feature {feature.Name}: duplicate name, ignoring");
                return false;
            }
            _features.Add(feature);
        }

        bool ok;
        try
        {
            ok = feature.Start();
        }
        catch (Exception e)
        {
            // A feature that throws on the way up must not take the host with
            // it: mark it faulted and let the others run.
            Log.Error($"feature {feature.Name}: mount threw", e);
            ok = false;
        }
        feature.State = ok ? FeatureState.Mounted : FeatureState.Faulted;
        Log.Info($"feature {feature.Name}: {(ok ? "mounted" : "mount failed")}");
        return ok;
    }

    /// <summary>Report a failure that happened on a feature's own thread after
    /// it mounted. The host logs it; whether the process survives is the
    /// host's policy decision (see AppHost.OnFeatureFaulted).</summary>
    public void Fault(IAppFeature feature, Exception e)
    {
        feature.State = FeatureState.Faulted;
        Log.Error($"feature {feature.Name}: faulted", e);
    }

    /// <summary>Stop every mounted feature, newest first (a feature may depend
    /// on one mounted before it). Idempotent.</summary>
    public void UnmountAll(string reason)
    {
        IAppFeature[] features;
        lock (_gate)
        {
            if (_unmounted)
            {
                return;
            }
            _unmounted = true;
            features = _features.ToArray();
        }
        for (int i = features.Length - 1; i >= 0; i--)
        {
            try
            {
                features[i].Stop();
            }
            catch (Exception e)
            {
                Log.Error($"feature {features[i].Name}: unmount threw", e);
            }
        }
        Log.Info($"core: features unmounted ({reason})");
    }
}
