// rst-c's own (not vendored): the add-in report dialog (spec #16, WU3's "Add-in Check"; spec #18).

using System.Windows;
using RST.UI.Theming;

namespace RST.UI.Loader;

/// <summary>What changed and what could not, after Load, Unload, Restore All or the v3.0 move; closed with Continue.</summary>
public partial class AddinReportWindow : Window
{
    public AddinReportWindow(AddinReportModel model)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        DataContext = model;
    }

    // DialogResult from the click handler closes the window after the click; nothing runs in Closing.
    private void OnContinue(object sender, RoutedEventArgs e) => DialogResult = true;
}
