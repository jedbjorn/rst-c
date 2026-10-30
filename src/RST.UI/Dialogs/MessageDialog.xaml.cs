// Adapted from splash-it src/SplashIt.UI/Dialogs/MessageDialog.xaml.cs (splash-it commit ba86a71; originally gantt-it src/GanttIt.Revit/UI/Dialogs/MessageDialog.xaml.cs, gantt-it commit a96d0a1), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); splash-it's DiscardChangesId is kept (the Builder's unsaved-changes prompt);
// an optional second action button between Confirm and Cancel (MessageDialogModel.SecondaryText) and the Choice it records
// (IDialogService.Choose, the Builder's Save & Exit / Discard & Exit / Cancel prompt).

using System.Windows;
using System.Windows.Automation;
using RST.UI.Theming;
using RST.UI.Resources;

namespace RST.UI.Dialogs;

/// <summary>
/// A small modal message or confirmation. One window class serves every
/// message so they look alike; the window's AutomationId says which one it is.
/// </summary>
public partial class MessageDialog : Window
{
    public const string MessageId = "MessageDialog";
    public const string DiscardChangesId = "DiscardChangesDialog";

    public MessageDialog(MessageDialogModel model, string automationId)
    {
        ArgumentNullException.ThrowIfNull(model);
        InitializeComponent();
        DarkTitleBar.Attach(this);
        DataContext = model;
        AutomationProperties.SetAutomationId(this, automationId);
        if (model.Danger)
        {
            // Destructive confirmations: Enter means Cancel; deleting takes a deliberate click.
            ConfirmButton.Style = (Style)FindResource("DangerFilledButton");
            ConfirmButton.IsDefault = false;
            CancelButton.IsDefault = true;
            Loaded += (_, _) => CancelButton.Focus();
        }
    }

    /// <summary>Which button closed the dialog: Confirm (primary), the second action, or Cancel.</summary>
    public DialogChoice Choice { get; private set; } = DialogChoice.Cancel;

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        Choice = DialogChoice.Primary;
        DialogResult = true;
    }

    private void OnSecondary(object sender, RoutedEventArgs e)
    {
        Choice = DialogChoice.Secondary;
        DialogResult = true;
    }
}

/// <summary>What a <see cref="MessageDialog"/> shows.</summary>
public sealed record MessageDialogModel(
    string Title,
    string Heading,
    string Message,
    IReadOnlyList<string> Items,
    string? ConfirmText = null,
    bool HasCancel = false,
    bool Danger = false)
{
    public bool HasItems => Items.Count > 0;

    /// <summary>A second action between Confirm and Cancel (null: none), e.g. "Discard &amp; Exit".</summary>
    public string? SecondaryText { get; init; }

    public bool HasSecondary => !string.IsNullOrEmpty(SecondaryText);

    /// <summary>The confirm button's text: as given, or OK in the UI language.</summary>
    public string ConfirmText { get; init; } = ConfirmText ?? UiStrings.Common_OK;
}
