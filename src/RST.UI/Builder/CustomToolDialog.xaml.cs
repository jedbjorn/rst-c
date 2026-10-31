// rst-c's own (not vendored): the Builder's Custom Tool dialog (feature #18, spec #19, WU4). All
// behavior is in CustomToolDialogModel; Confirm closes with true only when the label and address
// pass; the icon button opens the family icon picker in its own window (IconPickerWindow), since the
// picker's 700 px card does not fit this dialog.

using System.Windows;
using RST.UI.Dialogs;
using RST.UI.Theming;

namespace RST.UI.Builder;

public partial class CustomToolDialog : Window
{
    public CustomToolDialog(CustomToolDialogModel model)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        Model = model ?? throw new ArgumentNullException(nameof(model));
        DataContext = model;
        Loaded += (_, _) =>
        {
            LabelBox.Focus();
            LabelBox.SelectAll();
        };
    }

    public CustomToolDialogModel Model { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowPlacement.CapToWorkArea(this);
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (Model.TryConfirm()) DialogResult = true;
    }

    private void OnPickIcon(object sender, RoutedEventArgs e)
    {
        var picked = IconPickerWindow.Pick(this, Model.CreatePicker);
        if (picked.Committed) Model.IconFile = picked.Value;
    }
}
