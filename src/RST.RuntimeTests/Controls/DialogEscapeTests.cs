// rst-c's own (not vendored). Decision #62 ("escape should close all modals yes. not the tray app."):
// Escape cancels every dialog the add-in opens the way its Cancel, Close or Done does, through the
// shared EscapeGuard (SC-010, feature #18 WU6): a fresh press only, one the dialog's own content has
// not taken, and never past a dialog it has open, so one press closes the innermost modal only. The
// shared family dialogs take it when the add-in's DialogService or LoaderDialogs turns it on; the tray
// app's (both off by default) keep WPF's own Escape.

using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RST.Core.Activation;
using RST.Core.Builder;
using RST.Core.Colors;
using RST.Core.Theming;
using RST.RuntimeTests.Builder;
using RST.RuntimeTests.Loader;
using RST.RuntimeTests.Support;
using RST.UI.Builder;
using RST.UI.Dialogs;
using RST.UI.Loader;
using RST.UI.Swatches;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.Controls;

[Collection(WpfThemeCollection.Name)]
public class DialogEscapeTests
{
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

    private static KeyEventArgs EscapeArgs(Window w) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w)!, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent };

    /// <summary>Raises Escape on <paramref name="source"/> (as a key press bubbles up from it) and returns whether it was handled.</summary>
    private static bool PressEscape(UIElement source, Window w)
    {
        var args = EscapeArgs(w);
        source.RaiseEvent(args);
        WindowCapture.DoEvents();
        return args.Handled;
    }

    private static bool PressEscape(Window w) => PressEscape(w, w);

    private sealed class Shown<T>(T window) where T : Window
    {
        public T Window { get; } = window;

        public bool Closed { get; set; }

        public void CloseIfOpen()
        {
            if (!Closed) Window.Close();
        }
    }

    private static Shown<T> Show<T>(T window, Window? owner = null) where T : Window
    {
        if (owner is not null) window.Owner = owner;
        var shown = new Shown<T>(window);
        window.Closed += (_, _) => shown.Closed = true;
        WindowCapture.ShowOffScreen(window);
        return shown;
    }

    /// <summary>
    /// Runs <paramref name="open"/>, which shows a dialog modally and returns once it closes. When the
    /// dialog of type <typeparamref name="T"/> shows among <paramref name="owner"/>'s owned windows,
    /// Escape is pressed on it. Returns whether that press was handled and whether it closed the
    /// dialog; a dialog Escape left open, or one never found within 5 s, is closed so the test fails
    /// instead of hanging (the pattern of the Branding dialog's modal test).
    /// </summary>
    private static (bool Found, bool Handled, bool LeftOpen) EscapeWhileModal<T>(Window owner, Action open) where T : Window
    {
        var found = false;
        var handled = false;
        var leftOpen = false;
        var attempts = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            var dialog = owner.OwnedWindows.OfType<T>().FirstOrDefault(w => w.IsVisible);
            if (dialog is null && ++attempts < 50) return;
            timer.Stop();
            if (dialog is not null)
            {
                found = true;
                handled = PressEscape(dialog);
                if (!dialog.IsVisible) return;
                leftOpen = true;
                dialog.Close();
                return;
            }
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
        return (found, handled, leftOpen);
    }

    private static Window Owner()
    {
        var owner = new Window { Width = 400, Height = 300 };
        WindowCapture.ShowOffScreen(owner);
        return owner;
    }

    private static void AssertCancelledByEscape((bool Found, bool Handled, bool LeftOpen) run)
    {
        Assert.True(run.Found, "the dialog did not show among the owner's windows within 5 s");
        Assert.True(run.Handled, "Escape was not taken by the dialog");
        Assert.False(run.LeftOpen, "Escape left the dialog open");
    }

    // ── the rules ─────────────────────────────────────────────────────────

    /// <summary>A held Escape's repeats are swallowed by every dialog (KeyEventArgs.IsRepeat cannot be set from a test).</summary>
    [Theory]
    [InlineData(Key.Escape, true, true)]
    [InlineData(Key.Escape, false, false)]
    [InlineData(Key.Enter, true, false)]
    public void Only_a_held_Escape_is_swallowed(Key key, bool isRepeat, bool swallowed) =>
        Assert.Equal(swallowed, EscapeGuard.IsHeldEscape(key, isRepeat));

    /// <summary>
    /// A held Escape's repeat, as Windows sends it while the key stays down. KeyEventArgs.IsRepeat has
    /// no public setter; WPF's own KeyboardDevice sets it through the internal SetRepeat.
    /// </summary>
    private static KeyEventArgs HeldEscapeArgs(Window w, RoutedEvent routedEvent)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w)!, 0, Key.Escape) { RoutedEvent = routedEvent };
        var setRepeat = typeof(KeyEventArgs).GetMethod("SetRepeat", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(bool)])
                        ?? throw new MissingMethodException(nameof(KeyEventArgs), "SetRepeat");
        setRepeat.Invoke(args, [true]);
        Assert.True(args.IsRepeat, "the test's Escape is a repeat, so the test is not vacuous");
        return args;
    }

    /// <summary>
    /// The headline fix: on a real IsCancel dialog, a held Escape's repeat is swallowed in the
    /// tunnelling phase (EscapeGuard.CancelOnEscape's PreviewKeyDown hook), so neither WPF's IsCancel
    /// button (which acts on repeats, and only on unhandled presses) nor the dialog's own Escape
    /// cancels it. Without the hook the repeat stays unhandled and IsCancel would close the dialog:
    /// the press that closed the icon picker in front would, held, cancel this dialog too.
    /// </summary>
    [Fact]
    public void A_held_Escape_is_swallowed_and_never_cancels_the_dialog() => Sta(() =>
    {
        var model = CustomToolDialogModel.ForCreate("Frame", [], null);
        var o = Show(new CustomToolDialog(model));
        try
        {
            Assert.Contains(WindowCapture.Descendants<Button>(o.Window), b => b.IsCancel); // WPF's IsCancel acts on repeats

            var preview = HeldEscapeArgs(o.Window, Keyboard.PreviewKeyDownEvent);
            o.Window.LabelBox.RaiseEvent(preview);
            WindowCapture.DoEvents();
            Assert.True(preview.Handled, "the dialog swallows a held Escape before anything in it, IsCancel included, can act");
            Assert.False(o.Closed);

            var bubble = HeldEscapeArgs(o.Window, Keyboard.KeyDownEvent);
            o.Window.LabelBox.RaiseEvent(bubble);
            WindowCapture.DoEvents();
            Assert.False(o.Closed, "a repeat never cancels: only a fresh press does");
        }
        finally
        {
            o.CloseIfOpen();
        }
    });

    [Fact]
    public void Escape_that_the_dialogs_content_took_leaves_the_dialog() => Sta(() =>
    {
        var model = ProfileNameDialogModel.ForRename("Structure", _ => NameCheck.Ok);
        var o = Show(new ProfileNameDialog(model));
        try
        {
            var args = EscapeArgs(o.Window);
            args.Handled = true; // a drop-down or a text box that used it
            o.Window.NameBox.RaiseEvent(args);
            WindowCapture.DoEvents();
            Assert.False(o.Closed);
        }
        finally
        {
            o.CloseIfOpen();
        }
    });

    // ── the Builder's dialogs ─────────────────────────────────────────────

    [Fact]
    public void Escape_in_the_name_box_cancels_Rename() => Sta(() =>
    {
        var model = ProfileNameDialogModel.ForRename("Structure", _ => NameCheck.Ok);
        var o = Show(new ProfileNameDialog(model));
        try
        {
            o.Window.NameBox.Focus();
            model.Name = "Structure 2";
            Assert.True(PressEscape(o.Window.NameBox, o.Window));
            Assert.True(o.Closed);
            Assert.NotEqual(true, o.Window.DialogResult);
        }
        finally
        {
            o.CloseIfOpen();
        }
    });

    [Fact]
    public void Escape_cancels_New_Stack() => Sta(() =>
    {
        var o = Show(new StackDialog(StackDialogModel.ForNew("Frame", [])));
        try
        {
            Assert.True(PressEscape(o.Window));
            Assert.True(o.Closed);
            Assert.NotEqual(true, o.Window.DialogResult);
        }
        finally
        {
            o.CloseIfOpen();
        }
    });

    [Fact]
    public void Escape_in_the_label_box_cancels_the_custom_tool_dialog() => Sta(() =>
    {
        var model = CustomToolDialogModel.ForCreate("Frame", [], null);
        model.Label = "Wiki";
        model.Address = "wiki.studio.example";
        var o = Show(new CustomToolDialog(model));
        try
        {
            Assert.True(PressEscape(o.Window.LabelBox, o.Window));
            Assert.True(o.Closed);
            Assert.NotEqual(true, o.Window.DialogResult);
        }
        finally
        {
            o.CloseIfOpen();
        }
    });

    [Fact]
    public void Escape_closes_Branding_as_Done_does() => Sta(() =>
    {
        var model = new ProfileBuilderViewModel(BuilderFixtures.Backend(), new FakeDialogs(), new ManualClock().Schedule);
        model.Initialize();
        var o = Show(new BrandingDialog(model));
        try
        {
            Assert.True(PressEscape(o.Window));
            Assert.True(o.Closed);
        }
        finally
        {
            o.CloseIfOpen();
        }
    });

    [Fact]
    public void Escape_cancels_the_icon_picker_window() => Sta(() =>
    {
        IconPickerOutcome? outcome = null;
        var o = Show(new IconPickerWindow(closed => new RST.UI.Controls.IconPickerViewModel([], "wiki.png", null, result =>
        {
            outcome = result;
            closed(result);
        })));
        try
        {
            Assert.True(PressEscape(o.Window));
            Assert.True(o.Closed);
            Assert.NotNull(outcome);
            Assert.False(outcome!.Committed, "Escape is Cancel: the tool keeps its icon");
        }
        finally
        {
            o.CloseIfOpen();
        }
    });

    /// <summary>
    /// Nested modals: the custom tool dialog with its icon picker window open. Escape on the dialog
    /// does nothing (the picker is in front); Escape on the picker closes the picker only; the next
    /// press closes the dialog.
    /// </summary>
    [Fact]
    public void Escape_closes_the_innermost_modal_first() => Sta(() =>
    {
        var model = CustomToolDialogModel.ForCreate("Frame", [], null);
        var dialog = Show(new CustomToolDialog(model));
        var picker = Show(new IconPickerWindow(model.CreatePicker), dialog.Window);
        try
        {
            PressEscape(dialog.Window);
            Assert.False(dialog.Closed, "the picker is in front: the dialog behind keeps the press");
            Assert.False(picker.Closed);

            Assert.True(PressEscape(picker.Window));
            Assert.True(picker.Closed, "the innermost modal closes");
            Assert.False(dialog.Closed, "one press closes one modal");

            Assert.True(PressEscape(dialog.Window));
            Assert.True(dialog.Closed, "the next press closes the dialog");
        }
        finally
        {
            picker.CloseIfOpen();
            dialog.CloseIfOpen();
        }
    });

    // ── the family dialogs, as the add-in opens them ─────────────────────

    [Fact]
    public void Escape_closes_a_message_as_its_OK_does() => Sta(() =>
    {
        var owner = Owner();
        try
        {
            var service = new DialogService(owner) { EscapeCancels = true };
            AssertCancelledByEscape(EscapeWhileModal<MessageDialog>(owner, () => service.ShowMessage("Saved", "The profile was saved.")));
        }
        finally
        {
            owner.Close();
        }
    });

    [Fact]
    public void Escape_cancels_a_confirm() => Sta(() =>
    {
        var owner = Owner();
        try
        {
            var service = new DialogService(owner) { EscapeCancels = true };
            var confirmed = true;
            var run = EscapeWhileModal<MessageDialog>(owner, () =>
                confirmed = service.Confirm("Clean", "Delete selected junk?", [], "Continue", danger: true, "ConfirmClean"));
            AssertCancelledByEscape(run);
            Assert.False(confirmed, "Escape is Cancel, never the confirming action");
        }
        finally
        {
            owner.Close();
        }
    });

    [Fact]
    public void Escape_on_Save_or_Discard_is_Cancel() => Sta(() =>
    {
        var owner = Owner();
        try
        {
            var service = new DialogService(owner) { EscapeCancels = true };
            var choice = DialogChoice.Primary;
            var run = EscapeWhileModal<MessageDialog>(owner, () =>
                choice = service.Choose("Unsaved changes", "Save before you exit?", "Save & Exit", "Discard & Exit", MessageDialog.DiscardChangesId));
            AssertCancelledByEscape(run);
            Assert.Equal(DialogChoice.Cancel, choice);
        }
        finally
        {
            owner.Close();
        }
    });

    [Fact]
    public void Escape_cancels_the_colour_picker() => Sta(() =>
    {
        var owner = Owner();
        try
        {
            var service = new DialogService(owner) { EscapeCancels = true };
            Rgb? picked = Rgb.Parse("#123456");
            var run = EscapeWhileModal<ColorPickerDialog>(owner, () =>
                picked = service.PickColor(Rgb.Parse("#336699"), [Rgb.Parse("#336699")], "Panel colour"));
            AssertCancelledByEscape(run);
            Assert.Null(picked);
        }
        finally
        {
            owner.Close();
        }
    });

    [Fact]
    public void Escape_cancels_Confirm_Add_in_Changes() => Sta(() =>
    {
        var owner = Owner();
        try
        {
            var dialogs = new LoaderDialogs { Owner = owner, EscapeCancels = true };
            var confirmed = true;
            var run = EscapeWhileModal<ConfirmAddinChangesWindow>(owner, () =>
                confirmed = dialogs.ConfirmAddinChanges(new ConfirmAddinChangesModel(["Twinmotion (2026)"], [], [], [])));
            AssertCancelledByEscape(run);
            Assert.False(confirmed, "Escape is Cancel, never Confirm & Load");
        }
        finally
        {
            owner.Close();
        }
    });

    [Fact]
    public void Escape_closes_the_add_in_report_as_Continue_does() => Sta(() =>
    {
        var owner = Owner();
        try
        {
            var dialogs = new LoaderDialogs { Owner = owner, EscapeCancels = true };
            var report = AddinReportModel.ForUnload(new ActivationReport(ActivationIntent.Unload, [], []));
            AssertCancelledByEscape(EscapeWhileModal<AddinReportWindow>(owner, () => dialogs.ShowAddinReport(report)));
        }
        finally
        {
            owner.Close();
        }
    });

    /// <summary>
    /// The tray app shares DialogService and LoaderDialogs; decision #62 leaves it out, so by default
    /// neither attaches the add-in's Escape handling (the dialog's own IsCancel button stays WPF's).
    /// </summary>
    [Fact]
    public void The_shared_dialog_services_leave_Escape_alone_by_default() => Sta(() =>
    {
        Assert.False(new DialogService().EscapeCancels);
        Assert.False(new LoaderDialogs().EscapeCancels);
        var owner = Owner();
        try
        {
            var service = new DialogService(owner);
            var run = EscapeWhileModal<MessageDialog>(owner, () => service.ShowMessage("Notice", "Information only."));
            Assert.True(run.Found);
            Assert.False(run.Handled, "no add-in Escape handler on a tray-style dialog");
        }
        finally
        {
            owner.Close();
        }
    });
}
