// rst-c's own (not vendored): the Health window's code-behind (spec #16, WU2). The window binds to a
// HealthViewModel; HealthLauncher builds both, owns the window to Revit and shows it modally. Escape
// closes the window as Close does (SC-010, feature #18 WU6), and the Junk modal as its Cancel does
// while it is open (decision #62).

using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RST.UI.Dialogs;
using RST.UI.Mvvm;
using RST.UI.Theming;

namespace RST.UI.Health;

public partial class HealthWindow : Window
{
    /// <summary>Build after <c>RstTheme.Apply</c>: Theme.xaml reads the theme when the window loads it.</summary>
    public HealthWindow(HealthViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _junkFocus = new OverlayFocus(this);
        InitializeComponent();
        DarkTitleBar.Attach(this);
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
        viewModel.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) => UiGuard.Run("Health window: closed", () =>
        {
            viewModel.RequestClose -= OnRequestClose;
            viewModel.PropertyChanged -= OnViewModelChanged;
            viewModel.Dispose();
        });
    }

    public HealthViewModel ViewModel { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UiGuard.Run("Health window: place", () => WindowPlacement.CapToWorkArea(this));
    }

    private void OnRequestClose(object? sender, EventArgs e) => UiGuard.Run("Health window: close", Close);

    /// <summary>
    /// Escape closes Health the way Close does (SC-010): a fresh press only (a held Escape that closed a
    /// dialog in front must not repeat into Health), and only while Close is enabled, so never during a
    /// scan or a clean. Nothing else may want it: a dialog Health opened (an owned window), or a menu
    /// or popup open in it. While the Junk modal is open, Escape is its Cancel (decision #62): that
    /// press closes the modal only, and only while Cancel is enabled.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        UiGuard.Run("Health window: Escape", () =>
        {
            if (!EscapeGuard.IsFreshEscape(e.Key, e.IsRepeat, e.Handled)) return;
            if (ViewModel.IsJunkOpen)
            {
                if (!EscapeCancelsJunk) return;
                e.Handled = true;
                ViewModel.JunkCancelCommand.Execute(null);
                return;
            }
            if (!EscapeCloses) return;
            e.Handled = true;
            ViewModel.CloseCommand.Execute(null);
        });
    }

    private bool EscapeCancelsJunk =>
        ViewModel.JunkCancelCommand.CanExecute(null) && !EscapeGuard.HasSomethingOpen(this);

    private bool EscapeCloses =>
        !ViewModel.IsJunkOpen && ViewModel.CloseCommand.CanExecute(null) && !EscapeGuard.HasSomethingOpen(this);

    private readonly OverlayFocus _junkFocus;

    /// <summary>
    /// The Junk modal takes the keyboard focus when it opens (the content behind it is disabled). When it
    /// closes, focus goes back where it was (Clean junk files…), or into the window when that cannot take
    /// it, so Escape works on the next press (rc.3 VM finding F2, FocusReturn).
    /// </summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) => UiGuard.Run("Health window: focus the Junk modal", () =>
    {
        if (e.PropertyName != nameof(HealthViewModel.IsJunkOpen)) return;
        if (!ViewModel.IsJunkOpen)
        {
            _junkFocus.Closed();
            return;
        }
        _junkFocus.Opened();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            UiGuard.Run("Health window: focus the Junk modal", () => JunkCard.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))));
    });
}
