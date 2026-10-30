// rst-c's own (not vendored): the "Add-in Check" QA dialog (spec #16, WU3).

using System.Windows;
using RST.UI.Theming;

namespace RST.UI.Loader;

/// <summary>The required-add-in status after a load; always shown, closed with Continue.</summary>
public partial class AddinCheckWindow : Window
{
    public AddinCheckWindow(AddinCheckModel model)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        DataContext = model;
    }

    private void OnContinue(object sender, RoutedEventArgs e) => DialogResult = true;
}
