// rst-c's own (not vendored): the Health window's code-behind (spec #16, WU2). The window binds to a
// HealthViewModel; HealthLauncher builds both, owns the window to Revit and shows it modally. Escape
// closes the window as Close does (SC-010, feature #18 WU6).

using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
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
        viewModel.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) =>
        {
            viewModel.RequestClose -= OnRequestClose;
            viewModel.PropertyChanged -= OnViewModelChanged;
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

    /// <summary>
    /// Escape closes Health the way Close does (SC-010): a fresh press only (a held Escape that closed a
    /// dialog in front must not repeat into Health), and only while Close is enabled, so never during a
    /// scan or a clean. Nothing else may want it: the Junk modal, a dialog Health opened (an owned
    /// window), or a menu or popup open in it.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!EscapeGuard.IsFreshEscape(e.Key, e.IsRepeat, e.Handled) || !EscapeCloses) return;
        e.Handled = true;
        ViewModel.CloseCommand.Execute(null);
    }

    private bool EscapeCloses =>
        !ViewModel.IsJunkOpen && ViewModel.CloseCommand.CanExecute(null) && !EscapeGuard.HasSomethingOpen(this);

    /// <summary>The Junk modal takes the keyboard focus when it opens (the content behind it is disabled).</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(HealthViewModel.IsJunkOpen) || !ViewModel.IsJunkOpen) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => JunkCard.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)));
    }
}
