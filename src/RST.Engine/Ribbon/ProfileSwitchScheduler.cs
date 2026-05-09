// ProfileSwitchScheduler.cs — IExternalEvent shim that drives a live
// profile rebuild on Revit's UI thread.
//
// Why an ExternalEvent: WebView2 bridge callbacks run on whatever
// thread the COM marshaller picks; AdWindows mutations require the
// Revit UI thread. ExternalEvent.Raise() is the blessed mechanism for
// "do this on the UI thread when Revit's main loop is pumping" — it
// queues the work and fires the handler on the next idle tick after
// the modal Loader window closes (LoaderBridge calls Schedule before
// the UI returns to JS, JS then closes the window).
//
// Lifetime: one instance per Revit session, created at OnStartup and
// disposed at OnShutdown. NOT scoped to a single LoaderCommand
// invocation — disposing the ExternalEvent before Revit's idle pump
// fires it would silently swallow the queued switch.

using System;
using Autodesk.Revit.UI;
using RST.Core.Profiles;
using Serilog;

namespace RST.Engine.Ribbon;

internal sealed class ProfileSwitchScheduler : IProfileSwitchScheduler, IExternalEventHandler, IDisposable
{
    private readonly ExternalEvent _externalEvent;

    // Pending profile assignment is read inside Execute(); access from
    // Schedule() (any thread) and Execute() (UI thread) is serialised
    // by the lock. Most-recent-wins: if the user clicks Apply twice in
    // quick succession before the event fires, only the latest profile
    // is built.
    private readonly object _gate = new();
    private Profile? _pending;
    private bool _hasPending;
    private bool _disposed;

    public ProfileSwitchScheduler()
    {
        _externalEvent = ExternalEvent.Create(this);
    }

    public void Schedule(Profile? profile)
    {
        if (_disposed) return;
        lock (_gate)
        {
            _pending = profile;
            _hasPending = true;
        }
        try
        {
            _externalEvent.Raise();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ProfileSwitchScheduler.Schedule: ExternalEvent.Raise failed");
        }
    }

    public void Execute(UIApplication app)
    {
        Profile? toApply;
        bool fire;
        lock (_gate)
        {
            fire = _hasPending;
            toApply = _pending;
            _pending = null;
            _hasPending = false;
        }
        if (!fire) return;

        try
        {
            ProfileTabBuilder.BuildOrRebuild(app, toApply);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ProfileSwitchScheduler.Execute: live rebuild failed for profile={Name}",
                      toApply?.ProfileName ?? "(unload)");
        }

        // Apply the active profile's RSTify state on the rebuilt ribbon.
        // Loader has already written active_profile.json before raising
        // the event, so reading from disk gives us the canonical
        // hidden_tabs the user just selected. ApplyForActiveProfile
        // lifts whatever this session previously hid (so a switch from
        // {Architecture, Annotate} → {View} doesn't strand the first
        // two), applies the new set, and flips the icon. Mirrors the
        // OnApplicationInitialized path so live-switch and startup
        // can't drift.
        try
        {
            var active = ActiveProfile.Read();
            RstifyToggle.ApplyForActiveProfile(active.HiddenTabs);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ProfileSwitchScheduler.Execute: failed to apply RSTify state after live switch");
        }
    }

    public string GetName() => "RST.ProfileSwitchScheduler";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _externalEvent.Dispose(); }
        catch (Exception ex) { Log.Debug(ex, "ProfileSwitchScheduler.Dispose: external event dispose failed (non-fatal)"); }
    }
}
