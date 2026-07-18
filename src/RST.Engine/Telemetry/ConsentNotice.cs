// ConsentNotice.cs — the one-time first-run telemetry notice (spec
// Consent & Config): on the first enabled session where noticeShownUtc
// is null, a Revit TaskDialog at ApplicationInitialized says what is
// collected, that it stays on this machine, and where to see it or turn
// it off. noticeShownUtc is set regardless of how the dialog is
// dismissed. The show/mark rule lives in FirstRunNotice (RST.Core),
// where it is unit-tested; this is the Revit-facing glue.

using System;
using Autodesk.Revit.UI;
using RST.Core.Telemetry;

namespace RST.Engine.Telemetry;

internal static class ConsentNotice
{
    /// <summary>
    /// Show the notice if it is due. Gated on the collector actually
    /// collecting this session — a failed telemetry start must not
    /// announce collection that isn't happening (the notice then waits
    /// for the next session that collects). Never throws: a consent
    /// dialog that takes startup down would be worse than no dialog. A
    /// failed prefs write only means the notice reappears next session.
    /// </summary>
    public static void ShowIfDue(TelemetryCollector? collector, Action<string> log)
    {
        try
        {
            if (collector is null || !collector.IsEnabled) return;
            var prefs = TelemetryPrefs.Read();
            if (!FirstRunNotice.ShouldShow(prefs)) return;

            var dlg = new TaskDialog("RST")
            {
                MainInstruction = "RST keeps a local record of your Revit activity.",
                MainContent =
                    "To show you how you work, RST records session length, model opens and " +
                    "closes, sync-with-central times, and active minutes — no keystrokes, no " +
                    "model content.\n\n" +
                    "Everything stays on this computer. Nothing is sent anywhere.\n\n" +
                    "See your data — or turn collection off — any time under Health → Activity.",
                CommonButtons = TaskDialogCommonButtons.Close,
            };
            dlg.Show();

            if (!FirstRunNotice.MarkShown(prefs, DateTimeOffset.UtcNow, log: log))
                log("consent notice shown but not recorded — it will show again next session");
        }
        catch (Exception ex)
        {
            try { log("consent notice failed: " + ex.Message); }
            catch { /* logging must never take startup down */ }
        }
    }
}
