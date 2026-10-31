// rst-c's own (not vendored): ⚙ → Branding… (feature #18, spec #19, WU2), the machine-wide logo and
// link in a family dialog. Pick… takes the focus when it opens; closing it (Done, Escape or the
// title-bar ✕) commits the link box, as closing the old popover did.

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RST.UI.Dialogs;
using RST.UI.Theming;

namespace RST.UI.Builder;

public partial class BrandingDialog : Window
{
    public BrandingDialog(ProfileBuilderViewModel model)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        DataContext = model ?? throw new ArgumentNullException(nameof(model));
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => PickLogoButton.Focus());
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowPlacement.CapToWorkArea(this);
    }

    // The link is saved on change (blur or Enter); closing the dialog is a blur.
    protected override void OnClosing(CancelEventArgs e)
    {
        BrandingUrlBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        base.OnClosing(e);
    }
}
