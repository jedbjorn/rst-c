// rst-c's own (not vendored): the Builder's Rename… and Duplicate… dialog (feature #18, spec #19,
// WU2). The name box takes focus with its text selected; Confirm closes only when the name passes
// its check (ProfileNameDialogModel.TryConfirm), otherwise the refusal shows and the box keeps focus.

using System.Windows;
using System.Windows.Threading;
using RST.UI.Dialogs;
using RST.UI.Theming;

namespace RST.UI.Builder;

public partial class ProfileNameDialog : Window
{
    public ProfileNameDialog(ProfileNameDialogModel model)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        Model = model ?? throw new ArgumentNullException(nameof(model));
        DataContext = model;
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusName);
    }

    public ProfileNameDialogModel Model { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowPlacement.CapToWorkArea(this);
    }

    private void FocusName()
    {
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (Model.TryConfirm())
        {
            DialogResult = true;
            return;
        }
        FocusName();
    }
}
