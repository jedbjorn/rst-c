// Adapted from splash-it src/SplashIt.UI/Dialogs/DialogService.cs (splash-it commit ba86a71; originally gantt-it src/GanttIt.Revit/UI/Dialogs/DialogService.cs, gantt-it commit a96d0a1), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); comments say RST; SaveFile's default extension comes from the suggested file name (rst-c exports .zip, splash-it .json); Choose added (a three-way MessageDialog);
// EscapeCancels (decision #62: the add-in's dialogs take Escape by EscapeGuard's rules; the tray app's keep WPF's own).

using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using RST.Core.Colors;
using RST.UI.Swatches;

namespace RST.UI.Dialogs;

/// <summary>Opens RST's modal dialogs, each owned by the RST window that opens them.</summary>
public sealed class DialogService : IDialogService
{
    public DialogService(Window? owner = null) => Owner = owner;

    /// <summary>The window that owns the dialogs (the Loader, Builder or Health window).</summary>
    public Window? Owner { get; set; }

    /// <summary>
    /// Escape cancels the dialogs this service opens on a fresh press only, a message as its OK does
    /// (EscapeGuard.CancelOnEscape). The add-in's windows turn it on (decision #62); the tray app,
    /// which the decision leaves out, keeps WPF's own Escape (the Cancel button's).
    /// </summary>
    public bool EscapeCancels { get; init; }

    public void ShowMessage(string title, string message, string automationId = MessageDialog.MessageId) =>
        Show(new MessageDialogModel(title, title, message, []), automationId);

    public bool Confirm(string title, string message, IReadOnlyList<string> items, string confirmText, bool danger, string automationId) =>
        Show(new MessageDialogModel(title, title, message, items, confirmText, HasCancel: true, Danger: danger), automationId);

    public DialogChoice Choose(string title, string message, string primaryText, string secondaryText, string automationId)
    {
        var dialog = new MessageDialog(
            new MessageDialogModel(title, title, message, [], primaryText, HasCancel: true) { SecondaryText = secondaryText },
            automationId) { Owner = Owner };
        if (EscapeCancels) EscapeGuard.CancelOnEscape(dialog);
        return dialog.ShowDialog() == true ? dialog.Choice : DialogChoice.Cancel;
    }

    public Rgb? PickColor(Rgb current, IReadOnlyList<Rgb> palette, string title)
    {
        var workArea = Owner is null ? SystemParameters.WorkArea : MonitorWorkArea.Of(Owner);
        var dialog = new ColorPickerDialog(current, palette, title, PointerOnScreen(), workArea) { Owner = Owner };
        if (EscapeCancels) EscapeGuard.CancelOnEscape(dialog);
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public string? OpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true, Multiselect = false };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? SaveFile(string title, string filter, string fileName)
    {
        // The default extension follows the suggested name (rst-c exports profiles as .zip).
        var extension = System.IO.Path.GetExtension(fileName);
        var dialog = new SaveFileDialog
        {
            Title = title, Filter = filter, FileName = fileName, OverwritePrompt = true, AddExtension = true,
            DefaultExt = string.IsNullOrEmpty(extension) ? ".zip" : extension,
        };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    private bool Show(MessageDialogModel model, string automationId)
    {
        var dialog = new MessageDialog(model, automationId) { Owner = Owner };
        if (EscapeCancels) EscapeGuard.CancelOnEscape(dialog);
        return dialog.ShowDialog() == true;
    }

    /// <summary>The pointer position in screen DIPs, so the picker opens beside the chip that was clicked.</summary>
    private Point? PointerOnScreen()
    {
        if (Owner is null || PresentationSource.FromVisual(Owner) is not { CompositionTarget: { } target }) return null;
        var device = Owner.PointToScreen(Mouse.GetPosition(Owner));
        return target.TransformFromDevice.Transform(device);
    }
}
