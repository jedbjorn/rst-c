// rst-c's own (not vendored): the Builder's Junk Cleanup Targets modal (spec #16, WU5).

using System.Windows;
using RST.UI.Theming;

namespace RST.UI.Builder;

public partial class CleanupWindow : Window
{
    public CleanupWindow(CleanupViewModel model)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        DataContext = model ?? throw new ArgumentNullException(nameof(model));
    }

    private void OnApply(object sender, RoutedEventArgs e) => DialogResult = true;
}
