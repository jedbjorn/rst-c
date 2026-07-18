// HealthHost.cs — entry point used by RST.Engine's HealthCommand to open
// the health viewer without the Engine taking a direct dependency on
// WPF / WebView2 types at the call site.

using System;
using RST.Core.Health;

namespace RST.UI.Health;

public static class HealthHost
{
    /// <summary>
    /// Open the health viewer modally. Blocks until the user closes it.
    /// Must be called on the Revit UI thread.
    /// </summary>
    /// <param name="context">Captured Revit context (version, build,
    /// active model, warnings count). Pass <see cref="HealthContext.Empty"/>
    /// when running outside Revit — the scanner falls back to its
    /// system-only sections.</param>
    /// <param name="telemetryToggled">Invoked after the Activity tab's
    /// collection toggle persists a new enabled state — the collector
    /// seam (markers + heartbeat). Null = persist-only.</param>
    /// <param name="liveSessionStartUtc">Live read of the collector's
    /// session_start ts (see <see cref="HealthBridge"/>). Null = the
    /// context's captured value only.</param>
    public static void ShowModal(HealthContext context,
                                 Action<bool>? telemetryToggled = null,
                                 Func<DateTimeOffset?>? liveSessionStartUtc = null)
    {
        var window = new HealthWindow(context, telemetryToggled, liveSessionStartUtc);
        window.ShowDialog();
    }
}
