// SlotInvokeCommand.cs — System.Windows.Input.ICommand wired to
// AdWindows-built RibbonButtons.
//
// Mirrors SlotCommandBase semantics:
//   SlotKind.Url     → Process.Start (UrlNormalizer-canonicalised)
//   SlotKind.Command → UIApplication.PostCommand(RevitCommandId)
//
// AdWindows.RibbonButton.CommandHandler is an ICommand (WPF), not an
// IExternalCommand — so the dispatch runs WPF-side. For URL slots that
// is the entire operation. For Command slots we still need to bounce
// into Revit's main loop, which PostCommand handles for us; PostCommand
// is safe to call off-transaction and queues the command.

using System;
using System.Diagnostics;
using System.Windows.Input;
using Autodesk.Revit.UI;
using RST.Core.Ribbon;
using Serilog;

namespace RST.Engine.Ribbon;

internal sealed class SlotInvokeCommand : ICommand
{
    private readonly UIApplication _ui;
    private readonly SlotTarget _target;

    public SlotInvokeCommand(UIApplication ui, SlotTarget target)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _target = target ?? throw new ArgumentNullException(nameof(target));
    }

    // No CanExecute gating — slots are always clickable; if a target
    // is unavailable at click time (e.g. command id not registered in
    // this Revit version) we surface the error inside Execute().
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter)
    {
        try
        {
            switch (_target.Kind)
            {
                case SlotKind.Url:
                    Process.Start(new ProcessStartInfo(UrlNormalizer.Normalize(_target.Payload))
                    {
                        UseShellExecute = true,
                    });
                    return;

                case SlotKind.Command:
                    var id = RevitCommandId.LookupCommandId(_target.Payload);
                    if (id is null)
                    {
                        Log.Warning("SlotInvokeCommand: Revit command id not found ({CommandId}, display={DisplayName})",
                                    _target.Payload, _target.DisplayName);
                        ShowMissingAddinDialog(_target.DisplayName);
                        return;
                    }
                    _ui.PostCommand(id);
                    return;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SlotInvokeCommand failed (kind={Kind}, payload={Payload}, display={DisplayName})",
                      _target.Kind, _target.Payload, _target.DisplayName);
            ShowFailureDialog(_target.DisplayName, ex.Message);
        }
    }

    /// <summary>
    /// Surface a missing-target click via Revit's TaskDialog. The most
    /// common cause is the source add-in not being installed (or disabled
    /// in the Add-In Manager) for this Revit major. Defensive try/catch
    /// around .Show() — a click handler that throws while reporting an
    /// error is worse than the silent failure we're trying to fix.
    /// </summary>
    private static void ShowMissingAddinDialog(string displayName)
    {
        try
        {
            var dlg = new TaskDialog("RST")
            {
                MainInstruction = $"“{displayName}” couldn’t run.",
                MainContent =
                    "The add-in this button needs doesn’t appear to be installed in this Revit version " +
                    "(or it is disabled in Revit’s Add-In Manager). Install or enable the add-in and try again.",
                CommonButtons = TaskDialogCommonButtons.Close,
            };
            dlg.Show();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SlotInvokeCommand: missing-addin TaskDialog failed (display={DisplayName})", displayName);
        }
    }

    private static void ShowFailureDialog(string displayName, string error)
    {
        try
        {
            var dlg = new TaskDialog("RST")
            {
                MainInstruction = $"“{displayName}” couldn’t run.",
                MainContent = $"The button failed unexpectedly: {error}\n\nDetails are in %AppData%\\RST\\logs\\.",
                CommonButtons = TaskDialogCommonButtons.Close,
            };
            dlg.Show();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SlotInvokeCommand: failure TaskDialog failed (display={DisplayName})", displayName);
        }
    }
}
