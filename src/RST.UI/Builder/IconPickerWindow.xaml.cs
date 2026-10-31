// rst-c's own (not vendored): the family icon picker in its own window (feature #18, spec #19, WU4),
// for the Custom Tool dialog. The picker's model reports its outcome once (a committed value, or
// cancelled); the window closes on it.

using System.Windows;
using RST.Core.Builder;
using RST.UI.Controls;
using RST.UI.Dialogs;
using RST.UI.Theming;

namespace RST.UI.Builder;

public partial class IconPickerWindow : Window
{
    private IconPickerOutcome _outcome = new(false, null);

    internal IconPickerWindow(Func<Action<IconPickerOutcome>, IconPickerViewModel> create)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        Picker.DataContext = create(outcome =>
        {
            _outcome = outcome;
            if (IsVisible) Close();
        });
    }

    /// <summary>
    /// Shows the picker owned by <paramref name="owner"/> and returns its outcome; closing the
    /// window any other way is Cancel.
    /// </summary>
    public static IconPickerOutcome Pick(Window owner, Func<Action<IconPickerOutcome>, IconPickerViewModel> create)
    {
        var window = new IconPickerWindow(create) { Owner = owner };
        window.ShowDialog();
        return window._outcome;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowPlacement.CapToWorkArea(this);
    }
}
