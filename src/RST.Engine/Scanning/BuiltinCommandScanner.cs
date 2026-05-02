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

namespace RST.Engine.Scanning;

internal static class BuiltinCommandScanner
{
    public static IEnumerable<ScannedCommand> Enumerate()
    {
        foreach (PostableCommand cmd in Enum.GetValues(typeof(PostableCommand)))
        {
            string? id;
            try
            {
                id = RevitCommandId.LookupPostableCommandId(cmd)?.Name;
            }
            catch (Exception)
            {
                continue;
            }
            if (string.IsNullOrEmpty(id)) continue;

            yield return new ScannedCommand(
                Id: id!,
                DisplayName: cmd.ToString(),
                Origin: CommandOrigin.Native,
                AddinFile: null,
                AssemblyPath: null,
                SourceTab: null,
                SourcePanel: null);
        }
    }
}
