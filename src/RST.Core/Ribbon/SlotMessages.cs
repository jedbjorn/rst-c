// SlotMessages.cs — what a profile button's click tells the user when its command can't run
// (SlotInvokeCommand, RST.Engine). Kept here, on plain strings, so the wording is unit-tested.
//
// SC-051 (decision #63): Revit's PostCommand takes only PostableCommand and external-command ids
// (RST.Core.Scanning.PostCommandRule). The catalogue no longer lists the others, but a profile saved
// with v3.1.0 or earlier may still hold one (Text Types). Its button stays where the user put it;
// a click shows NotPostable, a plain message, not the "failed unexpectedly" one.

namespace RST.Core.Ribbon;

/// <summary>A TaskDialog's two texts.</summary>
public readonly record struct SlotMessage(string MainInstruction, string MainContent);

public static class SlotMessages
{
    /// <summary>The command id is not in this Revit at all: usually its add-in is missing or disabled.</summary>
    public static SlotMessage MissingAddin(string displayName) => new(
        $"“{displayName}” couldn’t run.",
        "The add-in this button needs doesn’t appear to be installed in this Revit version " +
        "(or it is disabled in Revit’s Add-In Manager). Install or enable the add-in and try again.");

    /// <summary>
    /// Revit refused to post the command: it is one of Revit's own tools that Revit doesn't let
    /// add-ins run (SC-051). Nothing failed, so the message says so and offers the way out.
    /// </summary>
    public static SlotMessage NotPostable(string displayName) => new(
        $"“{displayName}” can’t run from an RST button.",
        "Revit doesn’t let add-ins run this command, so RST can’t start it for you. " +
        "Use it from Revit’s own ribbon instead. You can remove this button from the profile in the Builder.");

    /// <summary>Anything else that went wrong.</summary>
    public static SlotMessage Failure(string displayName, string error) => new(
        $"“{displayName}” couldn’t run.",
        $"The button failed unexpectedly: {error}\n\nDetails are in %AppData%\\RST\\logs\\.");
}
