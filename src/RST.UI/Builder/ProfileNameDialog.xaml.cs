// rst-c's own (not vendored): the Builder's Rename… and Duplicate… dialog (feature #18, spec #19,
// WU2). The name box takes focus with its text selected; Confirm closes only when the name passes
// its check (ProfileNameDialogModel.TryConfirm), otherwise the refusal shows and the box keeps focus.

using System.Windows;
using System.Windows.Threading;
using RST.UI.Dialogs;
using RST.UI.Mvvm;
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
        Loaded += (_, _) => UiGuard.Run("Profile name dialog: loaded", () =>
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => UiGuard.Run("Profile name dialog: focus", FocusName)));
    }

    public ProfileNameDialogModel Model { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UiGuard.Run("Profile name dialog: place", () => WindowPlacement.CapToWorkArea(this));
    }

    private void FocusName()
    {
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => UiGuard.Run("Profile name dialog: confirm", () =>
    {
        if (Model.TryConfirm())
        {
            DialogResult = true;
            return;
        }
        FocusName();
    });
}
