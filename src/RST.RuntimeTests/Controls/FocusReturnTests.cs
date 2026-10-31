// rst-c's own (not vendored). v3.1.1, rc.3 VM finding F2: after Cancel in Edit custom tool, Escape did
// nothing in the Builder until a click into a control. The dialog had been opened from the Custom Tools
// row's ✎, which shows only while the row is hovered or holds focus; by the time the dialog closed it
// was collapsed, WPF's own focus restore failed, and WPF cleared keyboard focus, so no key event reached
// the Builder at all. These tests run the real windows and dialogs modally, with Win32 focus moving into
// the dialog and back, and press Escape the way WPF delivers a key: InputManager raises PreviewKeyDown
// and KeyDown on Keyboard.FocusedElement, and on nothing when no element has keyboard focus.

using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RST.Core.Colors;
using RST.Core.Loader;
using RST.Core.Theming;
using RST.RuntimeTests.Builder;
using RST.RuntimeTests.Health;
using RST.RuntimeTests.Loader;
using RST.RuntimeTests.Support;
using RST.UI.Builder;
using RST.UI.Dialogs;
using RST.UI.Health;
using RST.UI.Loader;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.Controls;

[Collection(WpfThemeCollection.Name)]
public sealed class FocusReturnTests : IDisposable
{
    private readonly TempFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    private static void Sta(Action action) => Screenshots.RunSta(() =>
    {
        try
        {
            RstTheme.Apply(UiTheme.Light);
            action();
        }
        finally
        {
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });

    private static string Id(DependencyObject d) => AutomationProperties.GetAutomationId(d);

    /// <summary>
    /// A key press as Windows delivers it to WPF: PreviewKeyDown (then, unhandled, KeyDown) is raised on
    /// the element with keyboard focus, and on nothing when none has it; then the key's release, so the
    /// next press is a fresh one, not a repeat.
    /// </summary>
    private static void PressKey(Window window, Key key)
    {
        var source = PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("the window is not showing");
        InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyUpEvent });
        WindowCapture.DoEvents();
    }

    /// <summary>A real click, as UI Automation (and a mouse) makes one: the button's OnClick, IsCancel included.</summary>
    private static void Invoke(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();

    private static void Settle()
    {
        for (var i = 0; i < 3; i++) WindowCapture.DoEvents();
    }

    private static string Describe(IInputElement? e) => e is null ? "nothing" : e is FrameworkElement f ? $"{f.GetType().Name} '{Id(f)}'" : e.GetType().Name;

    /// <summary>What the test saw while the dialog was open.</summary>
    private sealed class WhileOpen
    {
        public bool Found { get; set; }

        public bool FocusInDialog { get; set; }

        public bool OpenerHidden { get; set; }
    }

    /// <summary>
    /// Runs <paramref name="open"/> (which shows a dialog modally and returns once it closes); when the
    /// dialog of type <typeparamref name="T"/> shows among <paramref name="owner"/>'s owned windows,
    /// notes whether keyboard focus went into it and whether <paramref name="opener"/> is hidden, then
    /// closes it with <paramref name="close"/>. A dialog never found within 5 s, or still open 5 s after
    /// <paramref name="close"/>, is closed so the test fails instead of hanging.
    /// </summary>
    private static WhileOpen WhileModal<T>(Window owner, UIElement? opener, Action<T> close, Action open) where T : Window
    {
        var seen = new WhileOpen();
        var attempts = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            var dialog = owner.OwnedWindows.OfType<T>().FirstOrDefault(w => w.IsVisible);
            if (dialog is not null && !seen.Found)
            {
                seen.Found = true;
                seen.FocusInDialog = dialog.IsKeyboardFocusWithin;
                seen.OpenerHidden = opener is not null && !opener.IsVisible;
                close(dialog);
                return;
            }
            if (++attempts < 50) return;
            timer.Stop();
            foreach (var w in owner.OwnedWindows.OfType<Window>().ToList()) w.Close();
        };
        timer.Start();
        try
        {
            open();
        }
        finally
        {
            timer.Stop();
        }
        return seen;
    }

    // ── the Builder ───────────────────────────────────────────────────────

    private sealed record Builder(ProfileBuilderWindow Window, ProfileBuilderViewModel Model, FakeDialogs Dialogs)
    {
        public bool Closed { get; set; }
    }

    /// <summary>
    /// The Builder on Structure (the Custom Tools fixture), with the real Builder dialogs owned by it
    /// (as ProfileBuilderLauncher wires them) and <paramref name="dialogs"/> for the family dialogs.
    /// Shown off-screen and activated, so Win32 focus moves into its dialogs and back as in Revit.
    /// </summary>
    private Builder OpenBuilder(IDialogService? dialogs = null)
    {
        var backend = CustomToolFixtures.Backend(_dir.Path);
        var fake = new FakeDialogs();
        var builderDialogs = new BuilderDialogs();
        var model = new ProfileBuilderViewModel(backend, dialogs ?? fake, new ManualClock().Schedule, post: a => a(), builderDialogs: builderDialogs);
        model.Initialize();
        // As BuilderHarness does: from + New profile, then Structure, so it opens clean.
        if (model.SelectedProfileOption is { IsNew: false }) model.NewProfileCommand.Execute(null);
        model.SelectedProfileOption = model.ProfileOptions.Single(o => o.Entry?.Profile.ProfileName == "Structure");
        var window = new ProfileBuilderWindow(model);
        builderDialogs.Owner = window;
        var opened = new Builder(window, model, fake);
        window.Closed += (_, _) => opened.Closed = true;
        WindowCapture.ShowOffScreen(window);
        window.Activate();
        Settle();
        return opened;
    }

    private static void CloseBuilder(Builder b)
    {
        if (b.Closed) return;
        b.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Secondary);
        b.Window.Close();
    }

    private static Border Row(ProfileBuilderWindow w, string label) =>
        WindowCapture.Descendants<Border>(w.CatalogTreeView)
            .Single(b => Id(b) == "Builder.CustomTool" && b.IsVisible && ((CustomToolItemViewModel)b.DataContext).Label == label);

    private static Button RowButton(Border row, string id) => WindowCapture.Descendants<Button>(row).Single(b => Id(b) == id);

    /// <summary>
    /// Keyboard focus on the Custom Tools row's ✎ or ×, as a click on it leaves it: the row first (focus
    /// shows its ✎ ×, as hover does), then the button.
    /// </summary>
    private static Button FocusRowButton(ProfileBuilderWindow window, string label, string id)
    {
        var row = Row(window, label);
        Assert.True(row.Focus(), "the row takes keyboard focus (the window is active)");
        WindowCapture.DoEvents();
        var button = RowButton(row, id);
        Assert.True(button.IsVisible, "a focused row shows its ✎ ×");
        Assert.True(button.Focus());
        Assert.Same(button, Keyboard.FocusedElement);
        return button;
    }

    /// <summary>
    /// The F2 path: ✎ on a Custom Tools row opens Edit custom tool; Cancel closes it. The ✎ is hidden
    /// while the dialog has the keyboard (the row is neither hovered nor focused), so WPF cannot put focus
    /// back on it. Focus must still come back into the Builder, on that ✎ (the row shows it again once it
    /// holds focus), and the next Escape must reach the Builder: on a clean profile it closes it.
    /// </summary>
    [Fact]
    public void Cancel_in_Edit_custom_tool_returns_focus_and_the_next_Escape_reaches_the_Builder() => Sta(() =>
    {
        var b = OpenBuilder();
        try
        {
            Assert.False(b.Model.IsDirty);
            var edit = FocusRowButton(b.Window, "Wiki", "Builder.EditCustomToolEverywhere");

            var seen = WhileModal<CustomToolDialog>(b.Window, edit,
                dialog => Invoke(WindowCapture.Descendants<Button>(dialog).Single(x => Id(x) == "CustomToolDialog.Cancel")),
                () => edit.Command.Execute(edit.CommandParameter));
            Settle();

            Assert.True(seen.Found, "Edit custom tool did not show among the Builder's owned windows within 5 s");
            Assert.True(seen.FocusInDialog, "the dialog had the keyboard while it showed");
            Assert.True(seen.OpenerHidden, "the ✎ was hidden while the dialog showed (the case WPF's own restore cannot handle)");
            Assert.True(b.Window.IsKeyboardFocusWithin, $"focus is back in the Builder; it is on {Describe(Keyboard.FocusedElement)}");
            Assert.Same(edit, Keyboard.FocusedElement);

            PressKey(b.Window, Key.Escape);
            Assert.True(b.Closed, "the next Escape reached the Builder and closed it, as Exit does on a clean profile");
            Assert.Empty(b.Dialogs.ChooseCalls);
        }
        finally
        {
            CloseBuilder(b);
        }
    });

    /// <summary>
    /// The icon picker overlay (decision #62's in-window modal): opened from a tile's icon, closed by
    /// Escape. Focus goes back to that icon, not just somewhere in the window, and the next Escape
    /// closes the Builder.
    /// </summary>
    [Fact]
    public void Escape_on_the_icon_picker_returns_focus_to_the_icon_that_opened_it() => Sta(() =>
    {
        var b = OpenBuilder();
        try
        {
            var tile = WindowCapture.Descendants<Grid>(b.Window.PanelCards)
                .First(g => g.Name == "Tile" && g.DataContext is ToolTileViewModel { InStack: false });
            var icon = WindowCapture.Descendants<Button>(tile).Single(x => x.Name == ProfileBuilderWindow.IconHandleName);
            Assert.True(icon.Focus());
            icon.Command.Execute(icon.CommandParameter);
            Settle();
            Assert.True(b.Model.IsIconPickerOpen);
            Assert.True(b.Window.IconPickerOverlay.IsKeyboardFocusWithin, "the picker takes the keyboard when it opens");

            PressKey(b.Window, Key.Escape);
            Settle();
            Assert.False(b.Model.IsIconPickerOpen, "Escape closes the picker");
            Assert.False(b.Closed, "one press closes the innermost modal only");
            Assert.Same(icon, Keyboard.FocusedElement);

            PressKey(b.Window, Key.Escape);
            Assert.True(b.Closed, "the next Escape reached the Builder");
        }
        finally
        {
            CloseBuilder(b);
        }
    });

    /// <summary>
    /// The model's family dialogs: the real Confirm (a MessageDialog owned by the Builder, as
    /// ProfileBuilderLauncher's DialogService opens it), the fakes for the rest.
    /// </summary>
    private sealed class RealConfirm(FakeDialogs fake) : IDialogService
    {
        public DialogService Real { get; } = new() { EscapeCancels = true, ReturnsFocus = true };

        public void ShowMessage(string title, string message, string automationId = MessageDialog.MessageId) => fake.ShowMessage(title, message, automationId);

        public bool Confirm(string title, string message, IReadOnlyList<string> items, string confirmText, bool danger, string automationId) =>
            Real.Confirm(title, message, items, confirmText, danger, automationId);

        public DialogChoice Choose(string title, string message, string primaryText, string secondaryText, string automationId) =>
            fake.Choose(title, message, primaryText, secondaryText, automationId);

        public Rgb? PickColor(Rgb current, IReadOnlyList<Rgb> palette, string title) => fake.PickColor(current, palette, title);

        public string? OpenFile(string title, string filter) => fake.OpenFile(title, filter);

        public string? SaveFile(string title, string filter, string fileName) => fake.SaveFile(title, filter, fileName);
    }

    /// <summary>
    /// × on a Custom Tools row asks first (the real Confirm); Escape cancels it. The × is hidden while
    /// the confirm shows, as the ✎ is in F2; focus comes back on it, and the next Escape closes the Builder.
    /// </summary>
    [Fact]
    public void Escape_on_a_Builder_confirm_returns_focus_and_the_next_Escape_reaches_the_Builder() => Sta(() =>
    {
        var fake = new FakeDialogs();
        var dialogs = new RealConfirm(fake);
        var b = OpenBuilder(dialogs);
        dialogs.Real.Owner = b.Window;
        try
        {
            var remove = FocusRowButton(b.Window, "Wiki", "Builder.RemoveCustomToolEverywhere");

            var seen = WhileModal<MessageDialog>(b.Window, remove, dialog => PressKey(dialog, Key.Escape),
                () => remove.Command.Execute(remove.CommandParameter));
            Settle();

            Assert.True(seen.Found, "the confirm did not show among the Builder's owned windows within 5 s");
            Assert.True(seen.FocusInDialog, "the confirm had the keyboard while it showed");
            Assert.True(seen.OpenerHidden, "the × was hidden while the confirm showed");
            Assert.Contains("Wiki", WindowCapture.Descendants<Border>(b.Window.CatalogTreeView)
                .Where(x => Id(x) == "Builder.CustomTool").Select(x => ((CustomToolItemViewModel)x.DataContext).Label));
            Assert.True(b.Window.IsKeyboardFocusWithin, $"focus is back in the Builder; it is on {Describe(Keyboard.FocusedElement)}");
            Assert.Same(remove, Keyboard.FocusedElement);

            PressKey(b.Window, Key.Escape);
            Assert.True(b.Closed, "the next Escape reached the Builder");
            Assert.Empty(fake.ChooseCalls);
        }
        finally
        {
            CloseBuilder(b);
        }
    });

    // ── the Loader ───────────────────────────────────────────────────────

    /// <summary>
    /// Load Profile, with "Disable unused" on, asks Confirm Add-in Changes first (the real dialog, owned
    /// by the Loader through LoaderDialogs as ProfileLoaderLauncher wires it); Cancel closes it. Focus
    /// comes back on Load Profile, and the next Escape closes the Loader.
    /// </summary>
    [Fact]
    public void Cancel_in_Confirm_Add_in_Changes_returns_focus_and_the_next_Escape_closes_the_Loader() => Sta(() =>
    {
        var backend = LoaderFixtures.Backend();
        var dialogs = new LoaderTestDialogs();
        var loaderDialogs = new LoaderDialogs { EscapeCancels = true, ReturnsFocus = true };
        var model = new ProfileLoaderViewModel(backend, dialogs, loaderDialogs, new ManualClock().Schedule, _ => { });
        model.Initialize();
        var window = new ProfileLoaderWindow(model);
        loaderDialogs.Owner = window;
        var closed = false;
        window.Closed += (_, _) => closed = true;
        WindowCapture.ShowOffScreen(window);
        try
        {
            window.Activate();
            Settle();
            model.SelectedCard = model.Profiles.Single(c => c.Name == "Structure");
            model.DisableUnused = true;
            Settle();
            var load = WindowCapture.Descendants<Button>(window).Single(x => Id(x) == "Loader.Load");
            Assert.True(load.Focus(), "Load Profile takes keyboard focus (the window is active)");

            Task? loading = null;
            var seen = WhileModal<ConfirmAddinChangesWindow>(window, null,
                dialog => Invoke(dialog.CancelButton),
                () => loading = model.LoadAsync());
            Settle();
            Assert.True(loading!.IsCompleted, "a cancelled confirm ends the load before it starts");

            Assert.True(seen.Found, "Confirm Add-in Changes did not show among the Loader's owned windows within 5 s");
            Assert.True(seen.FocusInDialog, "the dialog had the keyboard while it showed");
            Assert.DoesNotContain("Load", backend.Calls);
            Assert.True(window.IsKeyboardFocusWithin, $"focus is back in the Loader; it is on {Describe(Keyboard.FocusedElement)}");
            Assert.Same(load, Keyboard.FocusedElement);

            PressKey(window, Key.Escape);
            Assert.True(closed, "the next Escape reached the Loader and closed it, as Close does");
        }
        finally
        {
            if (!closed) window.Close();
        }
    });

    /// <summary>
    /// The tray app shares DialogService and LoaderDialogs; decision #62 leaves it out, so by default
    /// neither returns focus itself (WPF's own restore stays, as before).
    /// </summary>
    [Fact]
    public void The_shared_dialog_services_leave_focus_to_WPF_by_default()
    {
        Assert.False(new DialogService().ReturnsFocus);
        Assert.False(new LoaderDialogs().ReturnsFocus);
    }

    // ── Health ───────────────────────────────────────────────────────────

    private static readonly DateTimeOffset Now = new(2026, 10, 30, 14, 0, 0, TimeSpan.Zero);

    private sealed class NoTimer : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed record Health(HealthWindow Window, HealthViewModel Model, FakeHealthService Service)
    {
        public bool Closed { get; set; }
    }

    /// <summary>Health over the fake service with cleanup targets, shown off-screen and activated.</summary>
    private static Health OpenHealth(IDialogService dialogs)
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        SynchronizationContext.SetSynchronizationContext(null);
        var service = new FakeHealthService { Snapshot = HealthSamples.Snapshot(), Targets = HealthSamples.Targets() };
        var vm = new HealthViewModel(service, dialogs, new HealthViewModelOptions
        {
            RunInBackground = work =>
            {
                work();
                return Task.CompletedTask;
            },
            StartTimer = (_, _) => new NoTimer(),
            Now = () => Now,
            Culture = culture,
            Zone = TimeZoneInfo.Utc,
            ToastSchedule = (_, _) => new NoTimer(),
        });
        vm.Load();
        var window = new HealthWindow(vm);
        var opened = new Health(window, vm, service);
        window.Closed += (_, _) => opened.Closed = true;
        WindowCapture.ShowOffScreen(window);
        window.Activate();
        Settle();
        return opened;
    }

    /// <summary>Clean junk files… focused, then clicked: the Junk modal opens and takes the keyboard.</summary>
    private static Button OpenJunkFromClean(Health h)
    {
        var clean = h.Window.CleanButton;
        Assert.True(clean.Focus(), "Clean junk files… takes keyboard focus (the window is active)");
        clean.Command.Execute(null);
        Settle();
        Assert.True(h.Model.IsJunkOpen);
        Assert.True(h.Window.JunkCard.IsKeyboardFocusWithin, "the modal takes the keyboard when it opens");
        Assert.False(clean.IsEnabled, "the content behind the modal is disabled");
        return clean;
    }

    /// <summary>
    /// Clean junk files… opens the Junk modal (the content behind it is disabled); Escape cancels it.
    /// Focus goes back on Clean junk files…, where it was (#169's note: Health left it on the window), and
    /// the next Escape closes Health.
    /// </summary>
    [Fact]
    public void Escape_on_the_Junk_modal_returns_focus_to_Clean_and_the_next_Escape_closes_Health() => Sta(() =>
    {
        var h = OpenHealth(new FakeDialogs());
        try
        {
            var clean = OpenJunkFromClean(h);

            PressKey(h.Window, Key.Escape);
            Settle();
            Assert.False(h.Model.IsJunkOpen, "Escape cancels the Junk modal");
            Assert.False(h.Closed, "one press closes the innermost modal only");
            Assert.Empty(h.Service.CleanCalls);
            Assert.Same(clean, Keyboard.FocusedElement);

            PressKey(h.Window, Key.Escape);
            Assert.True(h.Closed, "the next Escape reached Health and closed it");
        }
        finally
        {
            if (!h.Closed) h.Window.Close();
        }
    });

    /// <summary>
    /// Clean Selected closes the Junk modal and asks "Delete selected junk?" (the real Confirm, owned by
    /// Health as HealthLauncher's DialogService opens it); Escape cancels it. Focus goes back on Clean
    /// junk files… once the confirm has closed too, and the next Escape closes Health.
    /// </summary>
    [Fact]
    public void Escape_on_the_clean_confirm_returns_focus_to_Clean_and_the_next_Escape_closes_Health() => Sta(() =>
    {
        var dialogs = new RealConfirm(new FakeDialogs());
        var h = OpenHealth(dialogs);
        dialogs.Real.Owner = h.Window;
        try
        {
            var clean = OpenJunkFromClean(h);
            var run = WindowCapture.Descendants<Button>(h.Window).Single(x => Id(x) == "Health.JunkRun");

            var seen = WhileModal<MessageDialog>(h.Window, null, dialog => PressKey(dialog, Key.Escape),
                () => run.Command.Execute(null));
            Settle();

            Assert.True(seen.Found, "the clean confirm did not show among Health's owned windows within 5 s");
            Assert.True(seen.FocusInDialog, "the confirm had the keyboard while it showed");
            Assert.False(h.Model.IsJunkOpen);
            Assert.Empty(h.Service.CleanCalls);
            Assert.True(h.Window.IsKeyboardFocusWithin, $"focus is back in Health; it is on {Describe(Keyboard.FocusedElement)}");
            Assert.Same(clean, Keyboard.FocusedElement);

            PressKey(h.Window, Key.Escape);
            Assert.True(h.Closed, "the next Escape reached Health and closed it");
        }
        finally
        {
            if (!h.Closed) h.Window.Close();
        }
    });
}
