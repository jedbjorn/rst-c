// rst-c's own (not vendored); the window wiring follows splash-it
// src/SplashIt.UI/Settings/SettingsWindow.xaml.cs (splash-it commit ba86a71, MIT, same owner).
// The Profile Builder window (spec #16, WU4). All behavior is in ProfileBuilderViewModel; the
// code here is view plumbing: the title-bar ✕ asks the view-model first (Planner default 3), the
// Branding popover commits its URL box when it closes and does not reopen from the click that
// dismissed it, and the URL editor's label box takes focus when the editor opens.

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RST.UI.Dialogs;
using RST.UI.Theming;

namespace RST.UI.Builder;

public partial class ProfileBuilderWindow : Window
{
    public ProfileBuilderWindow(ProfileBuilderViewModel model)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        Model = model ?? throw new ArgumentNullException(nameof(model));
        DataContext = model;
        model.CloseRequested += () =>
        {
            if (IsVisible) Close();
        };
        model.PropertyChanged += (_, e) =>
        {
            // buildUrlEditor: the label box takes focus when the editor opens.
            if (e.PropertyName == nameof(ProfileBuilderViewModel.IsUrlEditorOpen) && model.IsUrlEditorOpen)
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => UrlLabelBox.Focus());
        };
    }

    public ProfileBuilderViewModel Model { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowPlacement.CapToWorkArea(this);
    }

    /// <summary>The title-bar ✕ goes through the unsaved-changes prompt, as Exit does.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!Model.CanClose()) e.Cancel = true;
        base.OnClosing(e);
    }

    // A click on Branding while the popover is open only closes it (the popup's own outside-click
    // close would otherwise be followed by the button reopening it).
    private void OnBrandingButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Model.IsBrandingOpen)
        {
            Model.IsBrandingOpen = false;
            e.Handled = true;
        }
        else if (Environment.TickCount64 - _brandingClosedAt < 300)
        {
            e.Handled = true;
        }
    }

    private long _brandingClosedAt = long.MinValue / 2;

    // The URL is saved on change (blur or Enter); closing the popover is a blur.
    private void OnBrandingClosed(object? sender, EventArgs e)
    {
        _brandingClosedAt = Environment.TickCount64;
        BrandingUrlBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }
}
