// rst-c's own (not vendored): opens the Health window for RST.Engine's HealthCommand (spec #16,
// WU2, Launchers), replacing v2.2.0's HealthHost. The Engine never touches WPF types.

using RST.Core.Health;
using RST.UI.Dialogs;
using Serilog;

namespace RST.UI.Health;

public static class HealthLauncher
{
    /// <summary>
    /// Opens the Health window modally, owned by <paramref name="ownerHwnd"/> (Revit's main window;
    /// zero opens it unowned and centred on the screen). Blocks until it closes. Revit UI thread
    /// only; call <c>RstTheme.Apply</c> (the Engine's <c>RevitThemeDetector.ApplyToWindows</c>) first.
    /// </summary>
    /// <param name="context">The Revit context captured before the window opens (<see cref="HealthContext.Empty"/> outside Revit).</param>
    /// <param name="telemetryToggled">Called with the clicked value on every successful collection toggle (the collector seam). Null: persist only.</param>
    /// <param name="liveSessionStartUtc">A live read of the collector's session start. Null: the context's captured value.</param>
    public static void Show(IntPtr ownerHwnd, HealthContext context,
                            Action<bool>? telemetryToggled = null,
                            Func<DateTimeOffset?>? liveSessionStartUtc = null)
    {
        var dialogs = new DialogService();
        var viewModel = new HealthViewModel(new HealthService(context, telemetryToggled, liveSessionStartUtc), dialogs);
        viewModel.Load();
        var window = new HealthWindow(viewModel);
        dialogs.Owner = window;
        WindowPlacement.Own(window, ownerHwnd);
        Log.Information("Health window opening (owner {Owner})", ownerHwnd == IntPtr.Zero ? "none" : "Revit");
        window.ShowDialog();
    }
}
