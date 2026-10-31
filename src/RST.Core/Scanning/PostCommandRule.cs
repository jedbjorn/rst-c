// PostCommandRule.cs — which command ids Revit lets an add-in run (SC-051, decision #63).
//
// RST runs a profile button through UIApplication.PostCommand. Revit's own rule for it (RevitAPIUI,
// UIApplication.CanPostCommand): "Only members of Autodesk.Revit.UI.PostableCommand or external
// commands can be posted." Any other id makes PostCommand throw "The commandId must be in
// Autodesk.Revit.UI.PostableCommand or an external command." (Text Types, ID_SETTINGS_TEXT, the
// arrow on Annotate > Text, in Revit 2026 and 2027: shared/evidence/v31-rc3, F1.)
//
// So the catalogue lists a ribbon item only when Revit lets add-ins post it (FnB, decision #63:
// "Some tools won't run with our method and that is fine"):
//   - its id is one of this Revit's PostableCommand ids (BuiltinCommandScanner's seed, read from
//     the running Revit every scan, so a new Revit year brings its own set); or
//   - its id is an external command's: Revit gives the buttons an add-in creates through the Revit
//     API ids that start with CustomCtrl_ (OriginClassifier.AddinButtonIdPrefix).
// Everything else (an internal Revit command the ribbon shows but PostableCommand does not hold, or
// a button an add-in drew straight into AdWindows) is left out, counted and logged by the scan.
//
// The rule is a rule, not a list: nothing here names Text Types. A saved profile that already holds
// such a button keeps it; its click shows a plain message (SlotMessages.NotPostable).

using System;

namespace RST.Core.Scanning;

public static class PostCommandRule
{
    /// <summary>True for the id of a button an add-in made through the Revit API (an external command).</summary>
    public static bool IsExternalCommandId(string? id) =>
        id is not null && id.StartsWith(OriginClassifier.AddinButtonIdPrefix, StringComparison.Ordinal);

    /// <summary>
    /// True when Revit lets an add-in post <paramref name="id"/>: an external command's id, or one
    /// that <paramref name="isPostableCommand"/> (this Revit's PostableCommand ids) holds.
    /// </summary>
    public static bool LetsAddinsPost(string? id, Func<string, bool> isPostableCommand)
    {
        if (isPostableCommand is null) throw new ArgumentNullException(nameof(isPostableCommand));
        if (string.IsNullOrEmpty(id)) return false;
        if (IsExternalCommandId(id)) return true;
        try { return isPostableCommand(id!); }
        catch { return false; }
    }
}
