// SlotCommandBase.cs — shared Execute() body for the slot pool.
//
// Each generated Slot## class derives from this and supplies its index.
// Execute looks up the target in SlotRegistry and posts the right
// command (or opens the URL).

using System;
using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using Serilog;

namespace RST.Engine.Ribbon;

[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public abstract class SlotCommandBase : IExternalCommand
{
    protected abstract int Index { get; }

    public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
    {
        var target = SlotRegistry.Get(Index);
        if (target is null)
        {
            Log.Warning("RST slot {Index} fired with no registered target", Index);
            message = $"RST slot {Index} is unbound — reload your profile.";
            return Result.Cancelled;
        }

        try
        {
            switch (target.Kind)
            {
                case SlotKind.Url:
                    Process.Start(new ProcessStartInfo(target.Payload) { UseShellExecute = true });
                    return Result.Succeeded;

                case SlotKind.Command:
                    var revitCmdId = RevitCommandId.LookupCommandId(target.Payload);
                    if (revitCmdId is null)
                    {
                        Log.Warning("RST slot {Index} target command not found: {CommandId}", Index, target.Payload);
                        message = $"Command not available in this Revit session: {target.Payload}";
                        return Result.Failed;
                    }
                    commandData.Application.PostCommand(revitCmdId);
                    return Result.Succeeded;

                default:
                    return Result.Failed;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RST slot {Index} ({DisplayName}) failed", Index, target.DisplayName);
            message = ex.Message;
            return Result.Failed;
        }
    }
}
