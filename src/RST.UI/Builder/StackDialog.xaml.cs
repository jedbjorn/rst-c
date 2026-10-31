// rst-c's own (not vendored): the Builder's New / Edit Stack dialog (feature #18, spec #19, WU3).
// All behavior is in StackDialogModel; Confirm closes with true only when it may (New: 2-3 ticked;
// Edit: always, fewer than 2 removes the stack).

using System.Windows;
using RST.UI.Dialogs;
using RST.UI.Theming;

namespace RST.UI.Builder;

public partial class StackDialog : Window
{
    public StackDialog(StackDialogModel model)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        Model = model ?? throw new ArgumentNullException(nameof(model));
        DataContext = model;
    }

    public StackDialogModel Model { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowPlacement.CapToWorkArea(this);
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (Model.CanConfirm) DialogResult = true;
    }
}
