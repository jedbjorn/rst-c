// rst-c's own. SC-046 / #172 review (a): source coverage catches new modal entries; these
// behavioural route tests prove the remaining WPF dialogs return the keyboard to their opener.
// Follow FocusReturnTests' STA, real modal routes and InputManager Escape helpers. Native file
// dialogs (and the native More colours dialog) need an interactive desktop/VM; OwnedWindows cannot
// drive them in CI. Health additions and top-level Revit focus remain separate work.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RST.Core.Activation;
using RST.Core.Logging;
using RST.RuntimeTests.Loader;
using RST.RuntimeTests.Support;
using RST.UI.Builder;
using RST.UI.Dialogs;
using RST.UI.Loader;
using RST.UI.Mvvm;
using RST.UI.Swatches;
using Xunit;

namespace RST.RuntimeTests.Controls;

public sealed partial class FocusReturnTests
{
    // Like EscapeCoverage, nameof makes a removed or renamed test fail compilation. Routes matter:
    // Rename/Duplicate and New/Edit Stack share a Window but must each exercise their real command.
    private static readonly Dictionary<string, string> FocusCoverage = new()
    {
        ["Builder.NewStack"] = nameof(Cancel_New_Stack_returns_focus_to_its_button),
        ["Builder.EditStack"] = nameof(Cancel_Edit_Stack_returns_focus_to_its_button),
        ["Builder.Rename"] = nameof(Cancel_Rename_returns_focus_to_the_Builder),
        ["Builder.Duplicate"] = nameof(Cancel_Duplicate_returns_focus_to_the_Builder),
        ["Builder.Branding"] = nameof(Branding_swaps_the_service_owner_and_returns_focus_to_the_Builder),
        ["CustomTool.IconPickerWindow"] = nameof(Icon_picker_returns_focus_to_Custom_Tool_before_Escape_returns_to_Builder),
        ["Builder.SaveDiscardChoose"] = nameof(Cancel_Save_Discard_keeps_the_Builder_open_with_focus),
        ["Builder.PickColor"] = nameof(Cancel_PickColor_returns_focus_to_the_panel_chip),
        ["Loader.AddinReport"] = nameof(Addin_report_returns_focus_to_the_Loader),
        ["Builder.EditCustomTool"] = nameof(Cancel_in_Edit_custom_tool_returns_focus_and_the_next_Escape_reaches_the_Builder),
        ["Builder.IconOverlay"] = nameof(Escape_on_the_icon_picker_returns_focus_to_the_icon_that_opened_it),
        ["Builder.Confirm"] = nameof(Escape_on_a_Builder_confirm_returns_focus_and_the_next_Escape_reaches_the_Builder),
        ["Loader.ConfirmAddinChanges"] = nameof(Cancel_in_Confirm_Add_in_Changes_returns_focus_and_the_next_Escape_closes_the_Loader),
    };

    [Fact]
    public void Focus_coverage_names_real_behavioural_tests()
    {
        foreach (var (route, test) in FocusCoverage)
        {
            var method = typeof(FocusReturnTests).GetMethod(test);
            Assert.True(method?.IsDefined(typeof(FactAttribute), inherit: true) == true, $"{route}: {test} must remain a test");
        }
    }

    [Fact]
    public void Failed_assertion_cleanup_returns_with_a_dirty_Builder_and_real_dialogs() => Sta(() =>
    {
        var service = new DialogService { ReturnsFocus = true, EscapeCancels = true };
        var b = OpenBuilder(service);
        service.Owner = b.Window;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var error = Record.Exception(() =>
        {
            try
            {
                b.Model.ProfileName += " edited";
                Assert.True(b.Model.IsDirty);
                Assert.Fail("SC-046 cleanup self-test");
            }
            finally { CloseBuilder(b); }
        });
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(error);
        Assert.Contains("SC-046 cleanup self-test", error!.Message);
        Assert.True(b.Model.IsAbandoned);
        Assert.False(b.Window.IsVisible);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
    });

    private sealed class FocusLogSink(List<LogEvent> events) : ILogSink
    {
        public void Emit(LogEvent logEvent, IReadOnlyDictionary<string, object?> properties)
        {
            lock (events) events.Add(logEvent);
        }

        public void Dispose() { }
    }

    private static void FocusRun(Action action) => Sta(() =>
    {
        var events = new List<LogEvent>();
        var faults = new List<Exception>();
        var previous = Log.Logger;
        using var logger = new Logger(LogLevel.Verbose, new FocusLogSink(events));
        void Unhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            faults.Add(e.Exception);
            e.Handled = true;
        }
        Dispatcher.CurrentDispatcher.UnhandledException += Unhandled;
        Log.Logger = logger;
        try
        {
            action();
            Settle();
            Assert.Empty(faults);
            lock (events)
                Assert.DoesNotContain(events, e => e.Level >= LogLevel.Error &&
                    (e.MessageTemplate == UiGuard.FaultTemplate || e.MessageTemplate == TaskGuard.FaultTemplate ||
                     e.MessageTemplate == WindowGuard.FaultTemplate));
        }
        finally
        {
            Log.Logger = previous;
            Dispatcher.CurrentDispatcher.UnhandledException -= Unhandled;
        }
    });

    private static void Returned(Window owner, UIElement opener, WhileOpen seen)
    {
        Settle();
        Assert.True(seen.Found, "the real route must open its owned dialog");
        Assert.True(seen.FocusInDialog, "the dialog must take the keyboard");
        Assert.True(owner.IsKeyboardFocusWithin, $"focus is back in the owner; it is on {Describe(Keyboard.FocusedElement)}");
        Assert.Same(opener, Keyboard.FocusedElement);
    }

    private static Button BuilderButton(Builder b, string id) =>
        WindowCapture.Descendants<Button>(b.Window).First(x => Id(x) == id && x.IsVisible && x.IsEnabled);

    private static void CancelBuilderDialog<T>(Builder b, UIElement opener, Action open) where T : Window
    {
        Assert.True(opener.Focus());
        var seen = WhileModal<T>(b.Window, opener, dialog => PressKey(dialog, Key.Escape), open);
        Returned(b.Window, opener, seen);
        Assert.False(b.Closed);
    }

    [Fact]
    public void Cancel_New_Stack_returns_focus_to_its_button() => FocusRun(() =>
    {
        var b = OpenBuilder();
        try
        {
            var button = BuilderButton(b, "Builder.NewStack");
            CancelBuilderDialog<StackDialog>(b, button, () => button.Command.Execute(button.CommandParameter));
            Assert.False(b.Model.IsDirty);
        }
        finally { CloseBuilder(b); }
    });

    [Fact]
    public void Cancel_Edit_Stack_returns_focus_to_its_button() => FocusRun(() =>
    {
        var b = OpenBuilder();
        try
        {
            var button = BuilderButton(b, "Builder.EditStack");
            CancelBuilderDialog<StackDialog>(b, button, () => button.Command.Execute(button.CommandParameter));
            Assert.False(b.Model.IsDirty);
        }
        finally { CloseBuilder(b); }
    });

    [Fact]
    public void Cancel_Rename_returns_focus_to_the_Builder() => FocusRun(() =>
    {
        var b = OpenBuilder();
        try
        {
            var button = BuilderButton(b, "Builder.Rename");
            CancelBuilderDialog<ProfileNameDialog>(b, button, () => b.Model.RenameCommand.Execute(null));
            Assert.False(b.Model.IsDirty);
        }
        finally { CloseBuilder(b); }
    });

    [Fact]
    public void Cancel_Duplicate_returns_focus_to_the_Builder() => FocusRun(() =>
    {
        var b = OpenBuilder();
        try
        {
            // The profile menu's opener is in the Builder; its menu closes before the command runs.
            CancelBuilderDialog<ProfileNameDialog>(b, b.Window.ProfileButton, () => b.Model.DuplicateCommand.Execute(null));
            Assert.False(b.Model.IsDirty);
        }
        finally { CloseBuilder(b); }
    });

    [Fact]
    public void Branding_swaps_the_service_owner_and_returns_focus_to_the_Builder() => FocusRun(() =>
    {
        var service = new DialogService { ReturnsFocus = true, EscapeCancels = true };
        var b = OpenBuilder(service);
        service.Owner = b.Window;
        try
        {
            var opener = b.Window.SettingsButton;
            Assert.True(opener.Focus());
            var seen = WhileModal<BrandingDialog>(b.Window, opener, branding =>
            {
                Assert.Same(branding, service.Owner);
                Assert.True(branding.PickLogoButton.Focus());
                // A real family dialog uses the swapped owner (without invoking a native file picker).
                var child = WhileModal<MessageDialog>(branding, branding.PickLogoButton,
                    dialog => PressKey(dialog, Key.Escape), () => service.ShowMessage("Branding", "Focus test"));
                Returned(branding, branding.PickLogoButton, child);
                PressKey(branding, Key.Escape);
            }, () => b.Model.BrandingCommand.Execute(null));
            Assert.Same(b.Window, service.Owner);
            Returned(b.Window, opener, seen);
        }
        finally { CloseBuilder(b); }
    });

    [Fact]
    public void Icon_picker_returns_focus_to_Custom_Tool_before_Escape_returns_to_Builder() => FocusRun(() =>
    {
        var b = OpenBuilder();
        try
        {
            var edit = FocusRowButton(b.Window, "Wiki", "Builder.EditCustomToolEverywhere");
            var seen = WhileModal<CustomToolDialog>(b.Window, edit, tool =>
            {
                Assert.True(tool.IconButton.Focus());
                var picker = WhileModal<IconPickerWindow>(tool, tool.IconButton,
                    dialog => PressKey(dialog, Key.Escape),
                    () => tool.IconButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                Returned(tool, tool.IconButton, picker);
                Assert.True(tool.IsVisible, "Escape closed only the picker");
                PressKey(tool, Key.Escape);
            }, () => edit.Command.Execute(edit.CommandParameter));
            Returned(b.Window, edit, seen);
            PressKey(b.Window, Key.Escape);
            Assert.True(b.Closed);
        }
        finally { CloseBuilder(b); }
    });

    [Fact]
    public void Cancel_Save_Discard_keeps_the_Builder_open_with_focus() => FocusRun(() =>
    {
        var service = new DialogService { ReturnsFocus = true, EscapeCancels = true };
        var b = OpenBuilder(service);
        service.Owner = b.Window;
        var originalName = b.Model.ProfileName;
        try
        {
            b.Model.ProfileName += " edited";
            Assert.True(b.Model.IsDirty);
            var exit = BuilderButton(b, "Builder.Exit");
            CancelBuilderDialog<MessageDialog>(b, exit, () => exit.Command.Execute(null));
            Assert.True(b.Model.IsDirty);
            Assert.True(b.Window.IsVisible);
        }
        finally
        {
            b.Model.ProfileName = originalName;
            CloseBuilder(b);
        }
    });

    [Fact]
    public void Cancel_PickColor_returns_focus_to_the_panel_chip() => FocusRun(() =>
    {
        var service = new DialogService { ReturnsFocus = true, EscapeCancels = true };
        var b = OpenBuilder(service);
        service.Owner = b.Window;
        try
        {
            var chip = BuilderButton(b, "Builder.PanelColor");
            CancelBuilderDialog<ColorPickerDialog>(b, chip, () => chip.Command.Execute(chip.CommandParameter));
            Assert.False(b.Model.IsDirty);
        }
        finally { CloseBuilder(b); }
    });

    [Fact]
    public void Addin_report_returns_focus_to_the_Loader() => FocusRun(() =>
    {
        var dialogs = new LoaderDialogs { ReturnsFocus = true, EscapeCancels = true };
        var model = new ProfileLoaderViewModel(LoaderFixtures.Backend(), new LoaderTestDialogs(), dialogs, new ManualClock().Schedule, _ => { });
        model.Initialize();
        var window = new ProfileLoaderWindow(model);
        dialogs.Owner = window;
        WindowCapture.ShowOffScreen(window);
        try
        {
            window.Activate();
            model.SelectedCard = model.Profiles.Single(c => c.Name == "Structure");
            Settle();
            var load = WindowCapture.Descendants<Button>(window).Single(x => Id(x) == "Loader.Load");
            Assert.True(load.Focus());
            var report = AddinReportModel.ForUnload(new ActivationReport(ActivationIntent.Unload, [], []));
            var seen = WhileModal<AddinReportWindow>(window, load, dialog => PressKey(dialog, Key.Escape),
                () => dialogs.ShowAddinReport(report));
            Returned(window, load, seen);
        }
        finally { window.Close(); }
    });
}
