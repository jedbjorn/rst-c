// Adapted from splash-it src/SplashIt.UI/Dialogs/IDialogService.cs (splash-it commit ba86a71; originally gantt-it src/GanttIt.Revit/UI/Dialogs/IDialogService.cs, gantt-it commit a96d0a1), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); comments say RST; Choose added (a three-way choice: the Builder's unsaved-changes prompt).

using RST.Core.Colors;

namespace RST.UI.Dialogs;

/// <summary>The modal dialogs the view-models open, so they never touch windows directly.</summary>
public interface IDialogService
{
    /// <summary>An informational or error message with one OK button.</summary>
    void ShowMessage(string title, string message, string automationId = MessageDialog.MessageId);

    /// <summary>A confirmation listing <paramref name="items"/>. True when the user confirms.</summary>
    bool Confirm(string title, string message, IReadOnlyList<string> items, string confirmText, bool danger, string automationId);

    /// <summary>
    /// A three-way choice: <paramref name="primaryText"/> (the default button), <paramref name="secondaryText"/>,
    /// and Cancel (Escape, or closing the dialog).
    /// </summary>
    DialogChoice Choose(string title, string message, string primaryText, string secondaryText, string automationId);

    /// <summary>The color picker, titled <paramref name="title"/>. Null when cancelled.</summary>
    Rgb? PickColor(Rgb current, IReadOnlyList<Rgb> palette, string title);

    /// <summary>The Windows Open dialog. The chosen path, or null when cancelled.</summary>
    string? OpenFile(string title, string filter);

    /// <summary>The Windows Save As dialog, starting with <paramref name="fileName"/>. The chosen path, or null when cancelled.</summary>
    string? SaveFile(string title, string filter, string fileName);
}

/// <summary>The answer to <see cref="IDialogService.Choose"/>.</summary>
public enum DialogChoice
{
    Cancel,
    Secondary,
    Primary,
}
