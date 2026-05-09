// HealthHost.cs — entry point used by RST.Engine's HealthCommand to open
// the health viewer without the Engine taking a direct dependency on
// WPF / WebView2 types at the call site.

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
    public static void ShowModal(HealthContext context)
    {
        var window = new HealthWindow(context);
        window.ShowDialog();
    }
}
