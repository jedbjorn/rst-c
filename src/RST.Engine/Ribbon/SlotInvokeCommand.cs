// SlotInvokeCommand.cs — System.Windows.Input.ICommand wired to
// AdWindows-built RibbonButtons.
//
// Dispatch (SlotKind and SlotTarget live in RST.Core.Ribbon, beside the
// layout planner that makes them):
//   SlotKind.Url     → Process.Start (UrlNormalizer-canonicalised)
//   SlotKind.Command → UIApplication.PostCommand(RevitCommandId)
// Large and stacked buttons (spec #17) both use it, one instance per
// button. Each click is logged, so a run can be checked from the log.
//
// AdWindows.RibbonButton.CommandHandler is an ICommand (WPF), not an
// IExternalCommand — so the dispatch runs WPF-side. For URL slots that
// is the entire operation. For Command slots we still need to bounce
// into Revit's main loop, which PostCommand handles for us; PostCommand
// is safe to call off-transaction and queues the command.
//
// SC-051 (decision #63): PostCommand throws Autodesk.Revit.Exceptions.
// ArgumentException when Revit doesn't let add-ins post the command (it
// is neither a PostableCommand nor an external command; Text Types was
// one). The catalogue leaves those out now, but a profile saved earlier
// may still hold one: its click logs a Warning and shows the plain
// SlotMessages.NotPostable message, not "failed unexpectedly", once
// UIApplication.CanPostCommand confirms Revit won't post it (otherwise
// the exception takes the failure path, ERR). Revit itself is asked, not
// a list, so a working button can never be stopped by a wrong guess here.
// An id LookupCommandId can't find gets SlotMessages.NotFound: "the
// add-in doesn't appear to be installed" for an add-in's id (CustomCtrl_
// or a manifest GUID), the plain message for any other (a Revit tool).
//
// The dialog texts live in RST.Core.Ribbon.SlotMessages (unit-tested).

using System;
using System.Diagnostics;
using System.Windows.Input;
using Autodesk.Revit.UI;
using RST.Core.Ribbon;
using RST.Core.Logging;

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
        Log.Information("SlotInvokeCommand: {Kind} {Payload} (display={DisplayName})",
                        _target.Kind, _target.Payload, _target.DisplayName);
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
                        // An add-in's id: its add-in is missing. Any other id: a Revit tool add-ins can't run.
                        Show(SlotMessages.NotFound(_target.Payload, _target.DisplayName), "not-found");
                        return;
                    }
                    try
                    {
                        _ui.PostCommand(id);
                    }
                    // Plain message only when Revit itself confirms it won't post this id; any other
                    // ArgumentException goes on to the failure path below (ERR).
                    catch (Autodesk.Revit.Exceptions.ArgumentException ex) when (CanPost(id) == false)
                    {
                        // "The commandId must be in Autodesk.Revit.UI.PostableCommand or an external command."
                        Log.Warning("SlotInvokeCommand: Revit doesn't let add-ins run {CommandId} (display={DisplayName}): {Error}",
                                    _target.Payload, _target.DisplayName, ex.Message);
                        Show(SlotMessages.NotPostable(_target.DisplayName), "not-postable");
                    }
                    return;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SlotInvokeCommand failed (kind={Kind}, payload={Payload}, display={DisplayName})",
                      _target.Kind, _target.Payload, _target.DisplayName);
            Show(SlotMessages.Failure(_target.DisplayName, ex.Message), "failure");
        }
    }

    /// <summary>
    /// UIApplication.CanPostCommand, guarded: it runs inside an exception filter, so it must never
    /// throw. Null when Revit couldn't answer (then the click takes the failure path).
    /// </summary>
    private bool? CanPost(RevitCommandId id)
    {
        try { return _ui.CanPostCommand(id); }
        catch (Exception ex)
        {
            Log.Debug(ex, "SlotInvokeCommand: CanPostCommand threw ({CommandId})", _target.Payload);
            return null;
        }
    }

    /// <summary>
    /// Surface a click that couldn't run via Revit's TaskDialog. Defensive
    /// try/catch around .Show() — a click handler that throws while
    /// reporting an error is worse than the silent failure we're trying to fix.
    /// </summary>
    private static void Show(SlotMessage message, string kind)
    {
        try
        {
            var dlg = new TaskDialog("RST")
            {
                MainInstruction = message.MainInstruction,
                MainContent = message.MainContent,
                CommonButtons = TaskDialogCommonButtons.Close,
            };
            dlg.Show();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SlotInvokeCommand: {Kind} TaskDialog failed ({Instruction})", kind, message.MainInstruction);
        }
    }
}
