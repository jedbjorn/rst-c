// rst-c's own (not vendored): the "Confirm Add-in Changes" dialog (spec #16, WU3).

using System.Windows;
using RST.UI.Theming;

namespace RST.UI.Loader;

/// <summary>Lists what the disable pass will do before Load Profile runs it; DialogResult true is Confirm &amp; Load.</summary>
public partial class ConfirmAddinChangesWindow : Window
{
    public ConfirmAddinChangesWindow(ConfirmAddinChangesModel model)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        DataContext = model;
        if (!model.HasTryDisable)
        {
            // No Try Disable column: the other two share the width.
            TryGap.Width = new GridLength(0);
            TryColumn.Width = new GridLength(0);
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
