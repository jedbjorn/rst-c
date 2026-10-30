// rst-c's own (not vendored): the Health window's code-behind (spec #16, WU2). The window binds to a
// HealthViewModel; HealthLauncher builds both, owns the window to Revit and shows it modally.

using System.Windows;
using RST.UI.Dialogs;
using RST.UI.Theming;

namespace RST.UI.Health;

public partial class HealthWindow : Window
{
    /// <summary>Build after <c>RstTheme.Apply</c>: Theme.xaml reads the theme when the window loads it.</summary>
    public HealthWindow(HealthViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DarkTitleBar.Attach(this);
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
        Closed += (_, _) =>
        {
            viewModel.RequestClose -= OnRequestClose;
            viewModel.Dispose();
        };
    }

    public HealthViewModel ViewModel { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowPlacement.CapToWorkArea(this);
    }

    private void OnRequestClose(object? sender, EventArgs e) => Close();
}
