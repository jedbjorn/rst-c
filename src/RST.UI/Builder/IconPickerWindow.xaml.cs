// rst-c's own (not vendored): the family icon picker in its own window (feature #18, spec #19, WU4),
// for the Custom Tool dialog. The picker's model reports its outcome once (a committed value, or
// cancelled); the window closes on it. Escape is the picker's own (EscapeGuard.CancelOnEscape).

using System.Windows;
using RST.Core.Builder;
using RST.UI.Controls;
using RST.UI.Dialogs;
using RST.UI.Mvvm;
using RST.UI.Theming;

namespace RST.UI.Builder;

public partial class IconPickerWindow : Window
{
    private IconPickerOutcome _outcome = new(false, null);

    internal IconPickerWindow(Func<Action<IconPickerOutcome>, IconPickerViewModel> create)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        var picker = create(outcome =>
        {
            _outcome = outcome;
            if (IsVisible) Close();
        });
        Picker.DataContext = picker;
        // The picker takes Escape while it holds the focus (the variant row first, then the picker);
        // a press on the window itself does the same, and a held Escape does nothing (decision #62).
        EscapeGuard.CancelOnEscape(this, picker.Escape);
    }

    /// <summary>
    /// Shows the picker owned by <paramref name="owner"/> and returns its outcome; closing the
    /// window any other way is Cancel.
    /// </summary>
    public static IconPickerOutcome Pick(Window owner, Func<Action<IconPickerOutcome>, IconPickerViewModel> create)
    {
        var window = new IconPickerWindow(create) { Owner = owner };
        FocusReturn.ShowDialog(window); // focus goes back into the Custom Tool dialog (rc.3 VM finding F2)
        return window._outcome;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UiGuard.Run("Icon picker window: place", () => WindowPlacement.CapToWorkArea(this));
    }
}
