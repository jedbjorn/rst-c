// rst-c's own (not vendored); the window wiring follows splash-it
// src/SplashIt.UI/Settings/SettingsWindow.xaml.cs (splash-it commit ba86a71, MIT, same owner).
// The Profile Loader window (spec #16, WU3). All behavior is in ProfileLoaderViewModel; Escape
// closes the window as Close does (SC-010, feature #18 WU6).

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using RST.UI.Dialogs;
using RST.UI.Theming;

namespace RST.UI.Loader;

public partial class ProfileLoaderWindow : Window
{
    private bool _rendered;

    public ProfileLoaderWindow(ProfileLoaderViewModel model)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        Model = model ?? throw new ArgumentNullException(nameof(model));
        DataContext = model;
        model.CloseRequested += () =>
        {
            if (IsVisible) Close();
        };

        // The active profile is selected once the window shows, so the "Adopt RSTify Presets?"
        // question (if any) opens over it.
        ContentRendered += (_, _) =>
        {
            if (_rendered) return;
            _rendered = true;
            Model.SelectActiveProfile();
        };

    }

    public ProfileLoaderViewModel Model { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowPlacement.CapToWorkArea(this);
    }

    /// <summary>The window stays open while the load's add-in pass runs (its result is still to show).</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (Model.IsWorking) e.Cancel = true;
        base.OnClosing(e);
    }

    /// <summary>
    /// Escape closes the Loader the way Close does (SC-010): only while Close is enabled, so never during
    /// a load or unload (the busy overlay) or the load's add-in pass, which the title-bar ✕ waits for
    /// too. Nothing else may want it: a dialog the Loader opened (an owned window), a popup or menu
    /// open in it, or a drag of the opacity slider (the mouse is captured).
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.Escape || !EscapeCloses) return;
        e.Handled = true;
        Model.CloseCommand.Execute(null);
    }

    private bool EscapeCloses =>
        !Model.IsWorking && Model.CloseCommand.CanExecute(null) && Mouse.Captured is null && !EscapeGuard.HasSomethingOpen(this);

    // The opacity override previews while dragging and is saved on release (the HTML's oninput /
    // onchange): a drag's end, a click on the track, or a key. The view-model saves only a changed value.
    private void OnOpacityDragCompleted(object sender, DragCompletedEventArgs e) => Model.CommitOpacityOverride();

    private void OnOpacityMouseUp(object sender, MouseButtonEventArgs e) => Model.CommitOpacityOverride();

    private void OnOpacityKeyUp(object sender, KeyEventArgs e) => Model.CommitOpacityOverride();
}
