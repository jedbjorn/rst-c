// ConsentToggle.cs — the decision half of the collection toggle
// (Activity tab footer): persist the clicked value through the shared
// merge-preserving prefs writer, then reconcile THIS process's
// collector with that value. The dialog-side plumbing (WebView2 bridge)
// lives UI-side; this piece is the testable rule, kept Revit-free per
// the placement rule — same split as FirstRunNotice.
//
// The reconcile is unconditional on a successful persist (SC-034): the
// on-disk state says nothing about this process's collector. Another
// Revit instance may have flipped the shared prefs file while our
// collector kept its old state — a toggle skipped because "disk already
// says that" would then report success while this instance keeps
// collecting (or stays silent) against the user's click. The collector
// seam (CollectorLifecycle.SetEnabled) is a no-op when local state
// already matches, so reconciling every click is safe and emits no
// duplicate markers.

namespace RST.Core.Telemetry;

public static class ConsentToggle
{
    /// <summary>
    /// Apply one consent click: serialize <paramref name="enabled"/>
    /// through <see cref="TelemetryPrefs.Update"/> (under the
    /// cross-process lock, against the freshest on-disk state — never a
    /// skip on a lock-free pre-read, which both races concurrent
    /// togglers and misses a local/disk divergence), then hand the same
    /// value to <paramref name="applyToCollector"/>. False = the
    /// preference did not persist; the collector is then left untouched
    /// so memory and disk cannot drift apart on a failed click. A
    /// throwing collector seam is swallowed (logged): the preference
    /// persisted, and that is what the click's success reports.
    /// </summary>
    public static bool Apply(
        bool enabled,
        Action<bool>? applyToCollector,
        string? path = null,
        Action<string>? log = null)
    {
        if (!TelemetryPrefs.Update(p => p.Enabled = enabled, path, log))
            return false;
        try
        {
            applyToCollector?.Invoke(enabled);
        }
        catch (Exception ex)
        {
            try { log?.Invoke("telemetry toggle: collector reconcile threw: " + ex.Message); }
            catch { /* logging must never throw */ }
        }
        return true;
    }
}
