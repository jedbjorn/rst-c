// rst-c's own (not vendored); the window wiring follows splash-it
// src/SplashIt.UI/Settings/SettingsWindow.xaml.cs (splash-it commit ba86a71, MIT, same owner).
// The Profile Loader window (spec #16, WU3). All behavior is in ProfileLoaderViewModel; Escape
// closes the window as Close does (SC-010, feature #18 WU6).

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using RST.Core.Logging;
using RST.UI.Dialogs;
using RST.UI.Mvvm;
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
        model.CloseRequested += () => UiGuard.Run("Loader window: close", () =>
        {
            if (IsVisible) Close();
        });

        // The active profile is selected once the window shows, so the "Adopt RSTify Presets?"
        // question (if any) opens over it. Posted, not run inside ContentRendered (SC-046, R3): the
        // selection may open a modal dialog (the RST 3.0 add-in report, the adopt question), and a
        // nested modal loop inside the render pass's event is how a fault there reached Revit.
        ContentRendered += (_, _) => UiGuard.Run("Loader window: rendered", () =>
        {
            if (_rendered) return;
            _rendered = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => UiGuard.Run("Loader window: select the active profile", Model.SelectActiveProfile));
        });
    }

    public ProfileLoaderViewModel Model { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UiGuard.Run("Loader window: place", () => WindowPlacement.CapToWorkArea(this));
    }

    /// <summary>The window stays open while the load's add-in pass runs (its result is still to show).</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        // SC-045: closed by the window safety net after an error, the window goes, working or not.
        if (WindowGuard.IsClosingAfterFault(this))
        {
            if (Model.IsWorking) Log.Warning("Loader: closed after an unexpected error while it was working; its result is not shown");
            base.OnClosing(e);
            return;
        }
        UiGuard.Run("Loader window: closing", () =>
        {
            if (Model.IsWorking) e.Cancel = true;
        });
        base.OnClosing(e);
    }

    /// <summary>
    /// Escape closes the Loader the way Close does (SC-010): a fresh press only (a held Escape that closed a
    /// dialog in front must not repeat into the Loader), and only while Close is enabled, so never during
    /// a load or unload (the busy overlay) or the load's add-in pass, which the title-bar ✕ waits for
    /// too. Nothing else may want it: a dialog the Loader opened (an owned window), a popup or menu
    /// open in it, or a drag of the opacity slider (the mouse is captured).
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        UiGuard.Run("Loader window: Escape", () =>
        {
            if (!EscapeGuard.IsFreshEscape(e.Key, e.IsRepeat, e.Handled) || !EscapeCloses) return;
            e.Handled = true;
            Model.CloseCommand.Execute(null);
        });
    }

    private bool EscapeCloses =>
        !Model.IsWorking && Model.CloseCommand.CanExecute(null) && Mouse.Captured is null && !EscapeGuard.HasSomethingOpen(this);

    // The opacity override previews while dragging and is saved on release (the HTML's oninput /
    // onchange): a drag's end, a click on the track, or a key. The view-model saves only a changed value.
    private void OnOpacityDragCompleted(object sender, DragCompletedEventArgs e) => UiGuard.Run("Loader window: opacity drag", Model.CommitOpacityOverride);

    private void OnOpacityMouseUp(object sender, MouseButtonEventArgs e) => UiGuard.Run("Loader window: opacity click", Model.CommitOpacityOverride);

    private void OnOpacityKeyUp(object sender, KeyEventArgs e) => UiGuard.Run("Loader window: opacity key", Model.CommitOpacityOverride);
}
