// rst-c's own (not vendored): the Builder's New / Edit Stack dialog (feature #18, spec #19, WU3).
// All behavior is in StackDialogModel; Confirm closes with true only when it may (New: 2-3 ticked;
// Edit: always, fewer than 2 removes the stack). Escape is Cancel, on a fresh press only
// (EscapeGuard.CancelOnEscape, decision #62).

using System.Windows;
using RST.UI.Dialogs;
using RST.UI.Mvvm;
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
        EscapeGuard.CancelOnEscape(this); // Cancel (decision #62)
    }

    public StackDialogModel Model { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UiGuard.Run("Stack dialog: place", () => WindowPlacement.CapToWorkArea(this));
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => UiGuard.Run("Stack dialog: confirm", () =>
    {
        if (Model.CanConfirm) DialogResult = true;
    });
}
