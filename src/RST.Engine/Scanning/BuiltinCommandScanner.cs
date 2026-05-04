// BuiltinCommandScanner.cs — enumerate Revit's built-in commands.
//
// Revit ships built-in commands as the PostableCommand enum.
// RevitCommandId.LookupPostableCommandId(cmd).Id surfaces the canonical
// "ID_BUTTON_*" string that profile commandIds reference.
//
// Some PostableCommand values fail to resolve on certain Revit majors
// (deprecated, conditionally available, or version-gated). Those are
// silently skipped — a missing built-in is not a scanner error.

using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using RST.Core.Scanning;
using Serilog;

namespace RST.Engine.Scanning;

internal static class BuiltinCommandScanner
{
    public static IEnumerable<ScannedCommand> Enumerate()
    {
        var skippedThrew = 0;
        var skippedEmpty = 0;
        var emitted = 0;
        var values = Enum.GetValues(typeof(PostableCommand));
        foreach (PostableCommand cmd in values)
        {
            string? id;
            try
            {
                id = RevitCommandId.LookupPostableCommandId(cmd)?.Name;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "BuiltinCommandScanner: lookup threw for {Cmd}", cmd);
                skippedThrew++;
                continue;
            }
            if (string.IsNullOrEmpty(id)) { skippedEmpty++; continue; }

            emitted++;
            yield return new ScannedCommand(
                Id: id!,
                DisplayName: cmd.ToString(),
                Origin: CommandOrigin.Native,
                AddinFile: null,
                AssemblyPath: null,
                SourceTab: null,
                SourcePanel: null);
        }
        Log.Debug("BuiltinCommandScanner: enum={EnumCount}, emitted={Emitted}, " +
                  "skippedEmpty={Empty}, skippedThrew={Threw}",
                  values.Length, emitted, skippedEmpty, skippedThrew);
    }
}
