// rst-c's own (not vendored): the "Confirm Add-in Changes" dialog (spec #16, WU3; spec #18).

using System.Windows;
using RST.UI.Theming;

namespace RST.UI.Loader;

/// <summary>Lists the add-in plan before Load Profile runs it; DialogResult true is Confirm &amp; Load.</summary>
public partial class ConfirmAddinChangesWindow : Window
{
    public ConfirmAddinChangesWindow(ConfirmAddinChangesModel model)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        DataContext = model;
        // Enter cancels; changing add-ins takes a deliberate click (family rule for destructive confirms).
        Loaded += (_, _) => CancelButton.Focus();
    }

    // DialogResult from the click handler closes the window after the click; nothing runs in Closing.
    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
