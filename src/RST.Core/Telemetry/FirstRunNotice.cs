// FirstRunNotice.cs — the decision + persistence half of the one-time
// consent notice (spec Consent & Config): show on the first ENABLED
// session where the notice has never been shown, and record that it was
// shown regardless of how the dialog was dismissed. The dialog itself
// is Revit UI (TaskDialog) and lives engine-side; this piece is the
// testable rule, kept Revit-free per the placement rule.

namespace RST.Core.Telemetry;

public static class FirstRunNotice
{
    /// <summary>The spec's trigger: collection enabled and the notice
    /// never shown. A disabled session shows nothing — the notice waits
    /// for the first session that actually collects.</summary>
    public static bool ShouldShow(TelemetryPrefs prefs) =>
        prefs.Enabled && prefs.NoticeShownUtc is null;

    /// <summary>
    /// Record the notice as shown (atomic prefs write, never throws).
    /// False on IO failure — the notice then reappears next session,
    /// erring on the side of re-informing, never a crash.
    /// </summary>
    public static bool MarkShown(
        TelemetryPrefs prefs, DateTimeOffset shownUtc,
        string? path = null, Action<string>? log = null)
    {
        prefs.NoticeShownUtc = shownUtc;
        return prefs.Write(path, log);
    }
}
