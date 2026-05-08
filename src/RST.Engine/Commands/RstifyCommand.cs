// RstifyCommand.cs — IExternalCommand wired to the "RSTify" ribbon button.
//
// Toggle the active profile's hidden_tabs against the live ribbon:
//   - hidden_tabs empty → TaskDialog telling the user to configure
//                        visibility in the Loader. No state change.
//   - hidden_tabs set, currently visible → hide them, swap icon to "on".
//   - hidden_tabs set, currently hidden  → show them, swap icon to "off".
//
// Mirrors /home/jedi/RST/RST.tab/Minify.panel/RSTify.pushbutton/script.py.

using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RST.Core.Profiles;
using RST.Engine.Ribbon;
using Serilog;

namespace RST.Engine.Commands;

[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class RstifyCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var active = ActiveProfile.Read();
            var hiddenTabs = active.HiddenTabs ?? System.Array.Empty<string>();
            Log.Information("=== RSTify clicked: hiddenTabs={Count} ===", hiddenTabs.Length);

            if (hiddenTabs.Length == 0)
            {
                TaskDialog.Show(
                    "RSTify",
                    "No tabs configured to hide.\n\nSet up tab visibility in the Loader (RSTify section).");
                return Result.Succeeded;
            }

            // Toggle: if any of the configured tabs is currently hidden,
            // we're in "hidden" state — show them. Otherwise hide them.
            bool currentlyHidden = RstifyToggle.IsCurrentlyHidden(hiddenTabs);
            int affected = currentlyHidden
                ? RstifyToggle.Show(hiddenTabs)
                : RstifyToggle.Hide(hiddenTabs);

            // After the toggle, "active" means tabs are now hidden.
            bool nowActive = !currentlyHidden;
            RstifyToggle.RefreshIcon(active: nowActive);

            Log.Information("=== RSTify done: nowActive={Active}, toggled {Affected}/{Asked} tabs in {Ms}ms ===",
                            nowActive, affected, hiddenTabs.Length, sw.ElapsedMilliseconds);
            return Result.Succeeded;
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "RSTify FAILED after {Ms}ms", sw.ElapsedMilliseconds);
            message = ex.Message;
            return Result.Failed;
        }
    }
}
