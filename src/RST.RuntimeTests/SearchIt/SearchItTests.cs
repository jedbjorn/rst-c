// rst-c's own (not vendored). Search-It (v3.4): the view-model's flows with a fake backend, and the
// real window: modeless and owned (no taskbar entry, owner set, Show returns with it open), centred on
// its owner with the keyboard in the search box on a cold and on a warm open, eight result cards at
// the default size, resizable but never remembered, the busy overlay drawn before a cold read and
// input queued during it dropped, the keys (decision #83: Enter makes a chip in the box and runs the
// highlighted tool on a card; Up, Down, typing and Escape), a left or right press outside closes while
// the middle button and the wheel do not, the keyboard taken back after a middle-button pan, another
// application in front closes, and a click on a result closes the window first and then runs the
// tool. Screenshots: search-it-*.

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RST.Core.Search;
using RST.Core.Theming;
using RST.RuntimeTests.Support;
using RST.UI.Dialogs;
using RST.UI.SearchIt;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.SearchIt;

[Collection(WpfThemeCollection.Name)]
public class SearchItTests
{
    private static void Sta(Action action, UiTheme theme = UiTheme.Light) => Screenshots.RunSta(() =>
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            RstTheme.Apply(theme);
            action();
        }
        finally
        {
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });

    public static TheoryData<UiTheme> Themes => new() { UiTheme.Light, UiTheme.Dark };

    private sealed class FakeBackend : ISearchItBackend
    {
        public IReadOnlyList<SearchTool> Tools { get; set; } = Sample;
        public List<SearchTool> Ran { get; } = [];
        public int Loads { get; private set; }
        public bool IsReady { get; set; }
        public Action? OnLoad { get; set; }
        public Action? OnRun { get; set; }

        public IReadOnlyList<SearchTool> LoadTools()
        {
            Loads++;
            OnLoad?.Invoke();
            return Tools;
        }

        public void Run(SearchTool tool)
        {
            OnRun?.Invoke();
            Ran.Add(tool);
        }
    }

    private static readonly SearchTool[] Sample =
    [
        new("ID_ALIGN", "Align", "Modify", "Modify"),
        new("ID_ANNOTATIONS_DIMENSION_ALIGNED", [new("Aligned Dimension", "Annotate", "Dimension"), new("Aligned Dimension", "Modify", "Measure")]),
        new("ID_OBJECTS_WALL", [new("Wall: Architectural", "Architecture", "Build"), new("Wall: Architectural", "Structure", "Structure"), new("Wall", "Massing & Site", "Model by Face")]),
        new("ID_OBJECTS_STRUCTURAL_WALL", "Wall: Structural", "Structure", "Structure"),
        new("ID_OBJECTS_CURTAIN_WALL", "Curtain Wall", "Architecture", "Build"),
        new("ID_OBJECTS_DOOR", "Door", "Architecture", "Build"),
        new("ID_OBJECTS_WINDOW", "Window", "Architecture", "Build"),
        new("ID_OBJECTS_FLOOR", "Floor: Architectural", "Architecture", "Build"),
        new("ID_OBJECTS_ROOF", "Roof by Footprint", "Architecture", "Build"),
        new("ID_VIEW_NEW_SECTION", "Section", "View", "Create"),
        new("ID_VIEW_NEW_3DVIEW", "Default 3D View", "View", "Create"),
        new("ID_EDIT_MOVE", "Move", "Modify", "Modify"),
        new("ID_OBJECTS_ROOM_TAG", [new("Tag Room", "Architecture", "Room & Area"), new("Room Tag", "Annotate", "Tag")]),
        new("CustomCtrl_%CustomCtrl_%Add-Ins%Kinship%Open", "Open Library", "Add-Ins", "Kinship"),
    ];

    /// <summary>A view-model with the tools read; the chosen tool's run is queued in <paramref name="posted"/> when given, else on the dispatcher.</summary>
    private static SearchItViewModel Loaded(FakeBackend backend, List<Action>? posted = null)
    {
        var vm = posted is null ? new SearchItViewModel(backend) : new SearchItViewModel(backend, posted.Add);
        vm.BeginLoading();
        vm.Load();
        return vm;
    }

    private static IEnumerable<string> Names(SearchItViewModel vm) => vm.Results.Select(r => r.Name);

    // ── the view-model ───────────────────────────────────────────────────

    [Fact]
    public void Typing_ranks_the_results_live_and_forgives_a_typo() => Sta(() =>
    {
        var vm = Loaded(new FakeBackend());
        Assert.Equal(Sample.Length, vm.Results.Count);
        Assert.Equal("14 tools", vm.StatusText);

        vm.Text = "algn";
        Assert.Equal("Align", Names(vm).First());
        vm.Text = "wall";
        Assert.Equal(["Wall", "Wall: Structural", "Curtain Wall"], Names(vm));
        Assert.Equal("3 of 14 tools", vm.StatusText);
        Assert.False(vm.IsEmpty);

        vm.Text = "zzzz";
        Assert.Empty(vm.Results);
        Assert.True(vm.IsEmpty);
        Assert.Equal("No tools match.", vm.EmptyText);
    });

    [Fact]
    public void A_result_card_shows_the_name_that_matched_and_where_that_is() => Sta(() =>
    {
        var vm = Loaded(new FakeBackend());
        vm.Text = "wall: arch";
        var card = Assert.Single(vm.Results);
        Assert.Equal("Wall: Architectural", card.Name);
        Assert.Equal("Architecture › Build", card.Location);
        Assert.True(card.HasMore);
        Assert.Equal("+2 more places", card.MoreText);
        Assert.Equal("Architecture › Build (+2 more places)", card.ToolTip);

        // The ribbon's other name for the same command: one card, under that name, at that place.
        vm.Text = "room tag";
        card = vm.Results[0];
        Assert.Equal("ID_OBJECTS_ROOM_TAG", card.Tool.Id);
        Assert.Equal("Room Tag", card.Name);
        Assert.Equal("Annotate › Tag", card.Location);
        Assert.Equal("+1 more place", card.MoreText);
        vm.Text = "tag room";
        Assert.Equal("Tag Room", vm.Results[0].Name);
        Assert.Equal("Architecture › Room & Area", vm.Results[0].Location);
        Assert.Single(vm.Results, r => r.Tool.Id == "ID_OBJECTS_ROOM_TAG");

        vm.Text = "align";
        Assert.False(vm.Results[0].HasMore);
        Assert.Equal("", vm.Results[0].MoreText);
        Assert.Equal("Modify › Modify", vm.Results[0].ToolTip);
    });

    [Fact]
    public void Enter_makes_a_chip_chips_are_ANDed_and_text_ranks_inside_them() => Sta(() =>
    {
        var backend = new FakeBackend();
        var vm = Loaded(backend);
        vm.Text = "  Wall ";
        Assert.True(vm.CommitChip());
        Assert.Equal("", vm.Text);
        Assert.Equal(["Wall"], vm.Chips.Select(c => c.Text));
        Assert.True(vm.HasChips);
        Assert.Equal(3, vm.Results.Count);
        Assert.Empty(backend.Ran); // a chip never runs a tool

        vm.Text = "strucural"; // a typo, ranked inside the chip's set
        Assert.Equal(["Wall: Structural"], Names(vm));

        vm.Text = "struct";
        Assert.True(vm.CommitChip());
        Assert.Equal(["Wall", "struct"], vm.Chips.Select(c => c.Text));
        Assert.Equal(["Wall: Structural"], Names(vm)); // "wall" AND "struct"
        // A chip is exact: as a chip, the typo that the text forgave finds nothing.
        vm.Text = "strucural";
        vm.CommitChip();
        Assert.Empty(vm.Results);
        Assert.True(vm.RemoveLastChip());

        // A blank box, or a chip that is already there, adds nothing.
        var before = vm.Chips.Count;
        vm.Text = "   ";
        Assert.False(vm.CommitChip());
        vm.Text = "WALL";
        Assert.False(vm.CommitChip());
        Assert.Equal(before, vm.Chips.Count);
        Assert.Equal("", vm.Text);
    });

    [Fact]
    public void A_chip_goes_with_its_x_or_with_Backspace_in_the_empty_box() => Sta(() =>
    {
        var vm = Loaded(new FakeBackend());
        foreach (var text in new[] { "wall", "arch" })
        {
            vm.Text = text;
            vm.CommitChip();
        }
        Assert.Equal(["Wall: Architectural"], Names(vm));

        vm.Text = "x";
        Assert.False(vm.RemoveLastChip()); // only in an empty box
        vm.Text = "";
        Assert.True(vm.RemoveLastChip());
        Assert.Equal(["wall"], vm.Chips.Select(c => c.Text));
        Assert.Equal(3, vm.Results.Count);

        var chip = vm.Chips[0];
        chip.RemoveCommand.Execute(chip);
        Assert.Empty(vm.Chips);
        Assert.False(vm.HasChips);
        Assert.Equal(Sample.Length, vm.Results.Count);
        Assert.False(vm.RemoveLastChip());
    });

    /// <summary>
    /// The window closes first; the run is queued behind the close, never made from inside the click
    /// (whatever the run shows must not open from the click handler of a window being destroyed).
    /// </summary>
    [Fact]
    public void Choosing_a_tool_closes_first_and_queues_its_run_once() => Sta(() =>
    {
        var order = new List<string>();
        var posted = new List<Action>();
        var backend = new FakeBackend { OnRun = () => order.Add("run") };
        var vm = Loaded(backend, posted);
        vm.RequestClose += (_, _) => order.Add("close");
        vm.Text = "door";
        var card = vm.Results[0];
        card.RunCommand.Execute(card);
        card.RunCommand.Execute(card); // a double click chooses once
        Assert.Equal(["close"], order);
        Assert.Empty(backend.Ran);
        Assert.Equal("ID_OBJECTS_DOOR", vm.Chosen?.Id);

        Assert.Single(posted)();
        Assert.Equal(["close", "run"], order);
        Assert.Equal("ID_OBJECTS_DOOR", Assert.Single(backend.Ran).Id);
    });

    [Fact]
    public void By_default_the_run_is_queued_on_the_dispatcher_behind_the_close() => Sta(() =>
    {
        var backend = new FakeBackend();
        var vm = Loaded(backend);
        var card = vm.Results[0];
        card.RunCommand.Execute(card);
        Assert.Empty(backend.Ran);
        WindowCapture.DoEvents();
        Assert.Same(card.Tool, Assert.Single(backend.Ran));
    });

    [Fact]
    public void While_the_tools_load_the_window_is_busy_and_what_was_typed_is_kept() => Sta(() =>
    {
        var backend = new FakeBackend();
        var posted = new List<Action>();
        var vm = new SearchItViewModel(backend, posted.Add);
        vm.BeginLoading();
        Assert.True(vm.IsLoading);
        Assert.True(vm.Busy.IsBusy);
        Assert.Empty(vm.Results);
        Assert.False(vm.IsEmpty, "no 'No tools match' while the tools are still being read");
        vm.Text = "door";
        vm.Load();
        Assert.False(vm.IsLoading);
        Assert.False(vm.Busy.IsBusy);
        Assert.Equal(["Door"], Names(vm));

        // A warm open reads without the busy state.
        var warm = new SearchItViewModel(new FakeBackend { IsReady = true });
        warm.Load();
        Assert.False(warm.Busy.IsBusy);
        Assert.Equal(Sample.Length, warm.Results.Count);

        // A backend that fails: the busy state still ends, and the failure reaches the caller (the command logs it).
        var failing = new SearchItViewModel(new FakeBackend { OnLoad = () => throw new InvalidOperationException("scan failed") });
        failing.BeginLoading();
        Assert.Throws<InvalidOperationException>(failing.Load);
        Assert.False(failing.IsLoading);
        Assert.False(failing.Busy.IsBusy);

        var none = Loaded(new FakeBackend { Tools = [] });
        Assert.Equal("RST found no tools to search.", none.EmptyText);
    });

    // ── the window ───────────────────────────────────────────────────────

    /// <summary>Nothing down, the pointer over the window, Revit's process in front.</summary>
    private static readonly PointerSample Idle = new(PointerInside: true, HostInFront: true);

    /// <summary>Nothing down, the pointer outside the window, Revit's process in front.</summary>
    private static readonly PointerSample Away = new(HostInFront: true);

    private sealed class Opened(SearchItWindow window, FakeBackend backend)
    {
        public SearchItWindow Window { get; } = window;
        public FakeBackend Backend { get; } = backend;
        public bool Closed { get; set; }
        public PointerSample Sample { get; set; } = Idle;
    }

    private static Opened Open(FakeBackend? backend = null)
    {
        backend ??= new FakeBackend();
        Opened? opened = null;
        var window = new SearchItWindow(Loaded(backend), () => opened!.Sample);
        opened = new Opened(window, backend);
        window.Closed += (_, _) => opened.Closed = true;
        WindowCapture.ShowOffScreen(window);
        return opened;
    }

    private static void CloseIfOpen(Opened o)
    {
        if (!o.Closed) o.Window.Close();
    }

    /// <summary>Raises a key press as of now (or of <paramref name="timestamp"/>) on <paramref name="source"/>; returns whether it was handled.</summary>
    private static bool Press(UIElement source, Window w, Key key, RoutedEvent? routed = null, int? timestamp = null)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w)!, timestamp ?? Environment.TickCount, key)
        {
            RoutedEvent = routed ?? Keyboard.KeyDownEvent,
        };
        source.RaiseEvent(args);
        WindowCapture.DoEvents();
        return args.Handled;
    }

    /// <summary>A key as the keyboard sends it: the tunnelling event first, then, when nothing took it, the bubbling one.</summary>
    private static bool Key2(UIElement source, Window w, Key key) =>
        Press(source, w, key, Keyboard.PreviewKeyDownEvent) || Press(source, w, key);

    private static bool Mouse2(UIElement source, RoutedEvent routed, int timestamp)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, timestamp, MouseButton.Left) { RoutedEvent = routed };
        source.RaiseEvent(args);
        return args.Handled;
    }

    private static void Type(UIElement source, string text)
    {
        var args = new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, source, text))
        {
            RoutedEvent = TextCompositionManager.PreviewTextInputEvent,
        };
        source.RaiseEvent(args);
        WindowCapture.DoEvents();
    }

    private static ListBoxItem Card(SearchItWindow window, int index) =>
        Assert.IsType<ListBoxItem>(window.ResultsList.ItemContainerGenerator.ContainerFromIndex(index));

    private static IEnumerable<SearchTool> Many(int count) =>
        Enumerable.Range(1, count).Select(i => new SearchTool("ID_" + i, $"Tool {i:000}", "Tab " + (i % 5), "Panel " + (i % 7)));

    [Fact]
    public void The_window_is_kept_off_the_taskbar_and_is_resizable() => Sta(() =>
    {
        var window = new SearchItWindow(Loaded(new FakeBackend()), () => Idle);
        Assert.False(window.ShowInTaskbar, "owned by Revit, it must not have its own taskbar entry");
        Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
        Assert.Equal(WindowStartupLocation.CenterOwner, window.WindowStartupLocation);
        window.Close();
    });

    private static Window OwnerOnScreen()
    {
        var owner = new Window
        {
            Width = 760, Height = 560, Left = 120, Top = 90, Title = "Owner",
            WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false,
        };
        owner.Show();
        WindowCapture.DoEvents();
        return owner;
    }

    /// <summary>
    /// The launcher shows it modeless (Show returns with the window open and no modal loop running),
    /// owned by the handle it was given, and a second call focuses the open window instead of opening
    /// another. A window that was only hidden (the safety net's fallback) does not count as open.
    /// </summary>
    [Fact]
    public void The_launcher_shows_it_modeless_and_owned_and_only_once() => Sta(() =>
    {
        var owner = OwnerOnScreen();
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        var backend = new FakeBackend();
        try
        {
            Assert.Null(SearchItLauncher.Current);
            Assert.True(SearchItLauncher.Show(ownerHandle, backend, () => Idle));
            var window = SearchItLauncher.Current;
            Assert.NotNull(window);
            Assert.True(window!.IsOpen);
            Assert.True(window.IsVisible, "Show returned and the window is still up: modeless");
            Assert.False(ComponentDispatcher.IsThreadModal, "no modal loop: Revit's view stays usable");
            Assert.Equal(ownerHandle, new WindowInteropHelper(window).Owner);
            Assert.False(window.ShowInTaskbar);
            Assert.Equal(1, backend.Loads);
            Assert.Equal(Sample.Length, window.ViewModel.Results.Count);
            Assert.False(window.ViewModel.IsLoading);

            Assert.False(SearchItLauncher.Show(ownerHandle, backend, () => Idle), "already open: focus it, do not open a second");
            Assert.Same(window, SearchItLauncher.Current);
            Assert.Equal(1, backend.Loads);

            window.Close();
            WindowCapture.DoEvents();
            Assert.Null(SearchItLauncher.Current);
            Assert.True(SearchItLauncher.Show(ownerHandle, backend, () => Idle), "closed: the next click opens a new one");
            var second = SearchItLauncher.Current!;
            Assert.NotSame(window, second);

            // Hidden, not closed (what WindowGuard falls back to when a close fails): not "open".
            var closed = false;
            second.Closed += (_, _) => closed = true;
            second.Hide();
            Assert.True(SearchItLauncher.Show(ownerHandle, backend, () => Idle), "a hidden window is replaced, not focused");
            Assert.NotSame(second, SearchItLauncher.Current);
            Assert.True(closed, "the stale window is closed for good");
            Assert.True(SearchItLauncher.Current!.IsVisible);
        }
        finally
        {
            SearchItLauncher.Current?.Close();
            owner.Close();
        }
    });

    /// <summary>
    /// The hard requirement: on open the window is centred over its owner (Revit's main window), active,
    /// with the keyboard and the caret in the search box, so the first keystroke after the ribbon click lands
    /// in the box. On a cold open (the tools read under the overlay) and on a warm one.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void It_opens_centred_on_its_owner_with_the_keyboard_in_the_search_box(bool cold) => Sta(() =>
    {
        var owner = OwnerOnScreen();
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        var backend = new FakeBackend { IsReady = !cold, Tools = Sample.Concat(Many(40)).ToArray() };
        bool? overlayAtLoad = null;
        var shownAtLoad = false;
        backend.OnLoad = () =>
        {
            shownAtLoad = SearchItLauncher.Current is { IsVisible: true };
            overlayAtLoad = SearchItLauncher.Current?.ViewModel.Busy.IsBusy;
        };
        try
        {
            Assert.True(SearchItLauncher.Show(ownerHandle, backend, () => Idle));
            var window = SearchItLauncher.Current!;
            // Straight after Show returns, as the command returns to Revit...
            AssertReadyToType(window, "when Show returns");
            // ...and once everything queued has run (the settle pass, the default-size pass).
            WindowCapture.DoEvents();
            AssertReadyToType(window, "once idle");

            if (cold)
            {
                Assert.True(shownAtLoad, "cold: the window was up while the tools were read");
                Assert.True(overlayAtLoad, "cold: under the busy overlay");
            }
            else
            {
                Assert.False(shownAtLoad, "warm: the tools were read before the window showed");
                Assert.Null(overlayAtLoad);
            }
            Assert.False(window.ViewModel.Busy.IsBusy);
            Assert.False(window.ViewModel.IsLoading);
            Assert.Equal(backend.Tools.Count, window.ViewModel.Results.Count);

            // Centred over the owner, both ways.
            var ownerCentre = new Point(owner.Left + owner.ActualWidth / 2, owner.Top + owner.ActualHeight / 2);
            var centre = new Point(window.Left + window.ActualWidth / 2, window.Top + window.ActualHeight / 2);
            Assert.True(Math.Abs(centre.X - ownerCentre.X) <= 12 && Math.Abs(centre.Y - ownerCentre.Y) <= 12,
                $"window centre {centre} (at {window.Left},{window.Top}, {window.ActualWidth}x{window.ActualHeight}), owner centre {ownerCentre}");

            // The first keystroke lands in the box: text typed at the focused element goes there.
            var focused = Assert.IsAssignableFrom<UIElement>(Keyboard.FocusedElement);
            Assert.Same(window.SearchBox, focused);
        }
        finally
        {
            SearchItLauncher.Current?.Close();
            owner.Close();
        }
    });

    private static void AssertReadyToType(SearchItWindow window, string when)
    {
        Assert.True(window.IsActive, $"the window is the active one ({when})");
        Assert.True(window.IsKeyboardFocusWithin, $"the keyboard is in the window ({when})");
        Assert.True(window.SearchBox.IsKeyboardFocused, $"the keyboard is in the search box ({when})");
        Assert.Equal(-1, window.ResultsList.SelectedIndex);
    }

    /// <summary>The busy overlay's frame is drawn (ContentRendered) before the tools are read: a cold catalogue build never freezes a blank window.</summary>
    [Fact]
    public void The_launcher_draws_the_busy_overlay_before_it_reads_the_tools() => Sta(() =>
    {
        var backend = new FakeBackend();
        bool? visible = null, busy = null, drawn = null;
        backend.OnLoad = () =>
        {
            var window = SearchItLauncher.Current;
            visible = window?.IsVisible;
            drawn = window?.FirstFrameDrawn;
            busy = window?.ViewModel.Busy.IsBusy;
        };
        try
        {
            SearchItLauncher.Show(IntPtr.Zero, backend, () => Idle);
            Assert.True(visible, "the window is shown before the tools are read");
            Assert.True(drawn, "and its first frame has been drawn (ContentRendered)");
            Assert.True(busy, "with the busy overlay up");
            Assert.False(SearchItLauncher.Current!.ViewModel.Busy.IsBusy);

            // A failing read closes the window and reaches the caller.
            SearchItLauncher.Current.Close();
            backend.OnLoad = () => throw new InvalidOperationException("scan failed");
            Assert.Throws<InvalidOperationException>(() => SearchItLauncher.Show(IntPtr.Zero, backend, () => Idle));
            WindowCapture.DoEvents();
            Assert.Null(SearchItLauncher.Current);
        }
        finally
        {
            SearchItLauncher.Current?.Close();
        }
    });

    /// <summary>
    /// A click or an Enter made while the tools were being read was aimed at the overlay. Delivered late,
    /// with its original time, it must not land on the result card now under the pointer and run a tool
    /// nobody chose. Input from after the read works as usual.
    /// </summary>
    [Fact]
    public void Input_stamped_before_the_tools_were_ready_is_dropped() => Sta(() =>
    {
        var backend = new FakeBackend();
        var during = 0;
        backend.OnLoad = () =>
        {
            Thread.Sleep(60);
            during = Environment.TickCount; // the user clicks and presses Enter here, while the UI thread is busy
            Thread.Sleep(60);
        };
        try
        {
            SearchItLauncher.Show(IntPtr.Zero, backend, () => Idle);
            var window = SearchItLauncher.Current!;
            WindowCapture.DoEvents();
            var card = WindowCapture.Descendants<Button>(window.ResultsList).First();

            Assert.True(Mouse2(card, Mouse.PreviewMouseDownEvent, during), "a queued press is swallowed");
            Assert.True(Mouse2(card, Mouse.PreviewMouseUpEvent, during), "and its release");
            window.FocusCard(0);
            Assert.True(Press(Card(window, 0), window, Key.Enter, Keyboard.PreviewKeyDownEvent, during), "a queued Enter is swallowed");
            WindowCapture.DoEvents();
            Assert.True(window.IsOpen);
            Assert.Null(window.ViewModel.Chosen);
            Assert.Empty(backend.Ran);
            // Typed text is not dropped: it was aimed at the search box.
            Assert.False(Press(window.SearchBox, window, Key.W, Keyboard.PreviewKeyDownEvent, during));

            // The same input, made now.
            Thread.Sleep(20);
            Assert.False(Mouse2(card, Mouse.PreviewMouseDownEvent, Environment.TickCount));
            Assert.False(Mouse2(card, Mouse.PreviewMouseUpEvent, Environment.TickCount));
            window.FocusCard(0);
            Assert.True(Press(Card(window, 0), window, Key.Enter, Keyboard.PreviewKeyDownEvent), "Enter on the card is taken, to run it");
            WindowCapture.DoEvents();
            Assert.False(window.IsOpen);
            Assert.Single(backend.Ran);
        }
        finally
        {
            SearchItLauncher.Current?.Close();
        }
    });

    [Fact]
    public void Eight_result_cards_show_by_default_a_resize_is_not_remembered() => Sta(() =>
    {
        var o = Open(new FakeBackend { Tools = Many(60).ToArray() });
        try
        {
            WindowCapture.DoEvents();
            var list = o.Window.ResultsList;
            Assert.True(Math.Abs(list.ActualHeight - SearchItLayout.VisibleRows * SearchItLayout.RowHeight) <= 1,
                $"list {list.ActualHeight}, window {o.Window.ActualHeight} (Height {o.Window.Height}), content {((FrameworkElement)o.Window.Content).ActualHeight}");
            Assert.Equal(8, FullyVisibleCards(list));
            Assert.Equal(60, o.Window.ViewModel.Results.Count); // the rest scroll
            Assert.Equal(SizeToContent.Manual, o.Window.SizeToContent);
            var defaultHeight = o.Window.ActualHeight;
            var defaultWidth = o.Window.ActualWidth;

            // Resizable: the list takes the extra height.
            o.Window.Height = defaultHeight + 2 * SearchItLayout.RowHeight;
            o.Window.Width = defaultWidth + 120;
            WindowCapture.DoEvents();
            Near(10 * SearchItLayout.RowHeight, list.ActualHeight);
            Assert.Equal(10, FullyVisibleCards(list));
            o.Window.Close();

            // Not persisted: the next window starts at the default size again.
            var next = Open(new FakeBackend { Tools = Many(60).ToArray() });
            try
            {
                WindowCapture.DoEvents();
                Near(defaultHeight, next.Window.ActualHeight);
                Near(defaultWidth, next.Window.ActualWidth);
                Assert.Equal(8, FullyVisibleCards(next.Window.ResultsList));
            }
            finally
            {
                CloseIfOpen(next);
            }
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    private static void Near(double expected, double actual) => Assert.InRange(actual, expected - 1, expected + 1);

    private static bool FullyVisible(ListBox list, ListBoxItem item)
    {
        if (!item.IsVisible) return false;
        var top = item.TransformToAncestor(list).Transform(new Point(0, 0)).Y;
        return top >= -0.5 && top + item.ActualHeight <= list.ActualHeight + 0.5;
    }

    private static int FullyVisibleCards(ListBox list) =>
        WindowCapture.Descendants<ListBoxItem>(list).Count(item => FullyVisible(list, item));

    [Fact]
    public void The_cursor_starts_in_the_search_box() => Sta(() =>
    {
        var o = Open();
        try
        {
            Assert.True(o.Window.SearchBox.IsFocused, "the search box has the window's focus as soon as it opens");
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    // ── keys (decision #83) ──────────────────────────────────────────────

    [Fact]
    public void Enter_in_the_box_makes_a_chip_and_runs_nothing_Backspace_removes_it() => Sta(() =>
    {
        var o = Open();
        try
        {
            var box = o.Window.SearchBox;
            box.Text = "wall";
            WindowCapture.DoEvents();
            Assert.Equal(3, o.Window.ViewModel.Results.Count);
            Assert.True(Key2(box, o.Window, Key.Enter));
            Assert.Equal(["wall"], o.Window.ViewModel.Chips.Select(c => c.Text));
            Assert.Equal("", box.Text);
            WindowCapture.DoEvents();
            Assert.Empty(o.Backend.Ran);
            Assert.Null(o.Window.ViewModel.Chosen);
            Assert.False(o.Closed, "Enter in the box keeps the window open");
            Assert.Equal(Visibility.Visible, o.Window.ChipList.Visibility);
            var remove = WindowCapture.Descendants<Button>(o.Window.ChipList).Single();
            Assert.NotNull(remove.Command);

            Assert.True(Press(box, o.Window, Key.Back, Keyboard.PreviewKeyDownEvent));
            Assert.Empty(o.Window.ViewModel.Chips);
            Assert.Equal(Visibility.Collapsed, o.Window.ChipList.Visibility);
            // With text in the box Backspace is the box's own.
            box.Text = "do";
            Assert.False(Press(box, o.Window, Key.Back, Keyboard.PreviewKeyDownEvent));
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    [Fact]
    public void Down_from_the_box_highlights_the_first_card_and_Up_and_Down_move_and_scroll() => Sta(() =>
    {
        var o = Open(new FakeBackend { Tools = Many(60).ToArray() });
        try
        {
            var window = o.Window;
            var list = window.ResultsList;
            Assert.Equal(-1, list.SelectedIndex);

            Assert.True(Key2(window.SearchBox, window, Key.Down));
            Assert.Equal(0, list.SelectedIndex);
            Assert.True(Card(window, 0).IsFocused, "the keyboard is on the first card");
            Assert.False(window.SearchBox.IsFocused);

            // The highlighted card is drawn as the theme's selected row; the others are not.
            Assert.True(Card(window, 0).IsSelected);
            var selected = Assert.IsType<SolidColorBrush>(Background(Card(window, 0)));
            Assert.Equal(((SolidColorBrush)window.FindResource("AccentSoftBrush")).Color, selected.Color);
            Assert.NotEqual(selected.Color, (Background(Card(window, 1)) as SolidColorBrush)?.Color);

            // Down past the eighth card: the list scrolls and the highlighted card stays in view.
            for (var i = 1; i <= 11; i++)
            {
                Assert.True(Key2(Card(window, i - 1), window, Key.Down));
                Assert.Equal(i, list.SelectedIndex);
                Assert.True(Card(window, i).IsFocused);
                Assert.True(FullyVisible(list, Card(window, i)), $"card {i} is in view");
            }
            Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem first && FullyVisible(list, first) ? first : null);

            // Up again, back into view at the top.
            for (var i = 10; i >= 0; i--)
            {
                Assert.True(Key2(Card(window, i + 1), window, Key.Up));
                Assert.Equal(i, list.SelectedIndex);
                Assert.True(FullyVisible(list, Card(window, i)), $"card {i} is in view");
            }

            // Up on the first card returns to the box; nothing is highlighted and nothing ran.
            Assert.True(Key2(Card(window, 0), window, Key.Up));
            Assert.True(window.SearchBox.IsFocused);
            Assert.Equal(-1, list.SelectedIndex);
            WindowCapture.DoEvents();
            Assert.Empty(o.Backend.Ran);
            Assert.False(o.Closed);

            // Down on the last card stays there.
            window.FocusCard(59);
            Assert.True(Key2(Card(window, 59), window, Key.Down));
            Assert.Equal(59, list.SelectedIndex);
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    private static Brush? Background(ListBoxItem card) => (card.Template.FindName("Bd", card) as Border)?.Background;

    [Fact]
    public void Enter_on_a_card_runs_the_highlighted_tool_as_a_click_does() => Sta(() =>
    {
        var o = Open();
        try
        {
            var window = o.Window;
            window.SearchBox.Text = "wall";
            WindowCapture.DoEvents();
            Assert.True(Key2(window.SearchBox, window, Key.Down));
            Assert.True(Key2(Card(window, 0), window, Key.Down));
            Assert.Equal(1, window.ResultsList.SelectedIndex);
            var closedWhenRun = false;
            o.Backend.OnRun = () => closedWhenRun = o.Closed;

            Assert.True(Key2(Card(window, 1), window, Key.Enter));
            WindowCapture.DoEvents();
            Assert.True(o.Closed);
            Assert.Equal("chosen", window.CloseReason);
            Assert.True(closedWhenRun, "the window is gone before the tool is posted");
            Assert.Equal("ID_OBJECTS_STRUCTURAL_WALL", Assert.Single(o.Backend.Ran).Id);
            Assert.Empty(window.ViewModel.Chips); // Enter on a card is not a chip
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    [Fact]
    public void Typing_on_a_card_goes_back_to_the_box_and_adds_the_character() => Sta(() =>
    {
        var o = Open();
        try
        {
            var window = o.Window;
            window.SearchBox.Text = "wal";
            window.SearchBox.CaretIndex = 3;
            WindowCapture.DoEvents();
            Assert.True(Key2(window.SearchBox, window, Key.Down));
            Assert.True(Card(window, 0).IsFocused);

            Type(Card(window, 0), "l");
            Assert.Equal("wall", window.SearchBox.Text);
            Assert.Equal("wall", window.ViewModel.Text);
            Assert.True(window.SearchBox.IsFocused, "the keyboard is back in the box");
            Assert.Equal(4, window.SearchBox.CaretIndex);
            Assert.Equal(-1, window.ResultsList.SelectedIndex);
            Assert.Empty(o.Backend.Ran);

            // Enter, Tab and the like are not characters: they do not type.
            Assert.True(Key2(window.SearchBox, window, Key.Down));
            Type(Card(window, 0), "\r");
            Assert.Equal("wall", window.SearchBox.Text);
            Assert.True(Card(window, 0).IsFocused);
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    [Fact]
    public void Escape_closes_Search_It_without_running_anything() => Sta(() =>
    {
        var o = Open();
        try
        {
            o.Window.SearchBox.Text = "door";
            Assert.True(Key2(o.Window.SearchBox, o.Window, Key.Escape));
            WindowCapture.DoEvents();
            Assert.True(o.Closed);
            Assert.Equal("escape", o.Window.CloseReason);
            Assert.Empty(o.Backend.Ran);
            Assert.Null(o.Window.ViewModel.Chosen);
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    [Fact]
    public void Escape_on_a_card_closes_it_too() => Sta(() =>
    {
        var o = Open();
        try
        {
            Assert.True(Key2(o.Window.SearchBox, o.Window, Key.Down));
            Assert.True(Key2(Card(o.Window, 0), o.Window, Key.Escape));
            WindowCapture.DoEvents();
            Assert.True(o.Closed);
            Assert.Equal("escape", o.Window.CloseReason);
            Assert.Empty(o.Backend.Ran);
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    [Fact]
    public void A_click_on_a_result_card_closes_the_window_and_runs_that_tool() => Sta(() =>
    {
        var o = Open();
        try
        {
            var closedWhenRun = false;
            o.Backend.OnRun = () => closedWhenRun = o.Closed;
            o.Window.SearchBox.Text = "door";
            WindowCapture.DoEvents();
            var card = WindowCapture.Descendants<Button>(o.Window.ResultsList).First();
            Assert.Equal("Door", ((SearchResultItem)card.DataContext).Name);
            card.Command.Execute(card.CommandParameter);
            Assert.True(o.Closed);
            Assert.Empty(o.Backend.Ran); // queued behind the close, not run from inside the click
            WindowCapture.DoEvents();
            Assert.True(closedWhenRun, "the window is gone before the tool is posted");
            Assert.Equal("chosen", o.Window.CloseReason);
            Assert.Equal("ID_OBJECTS_DOOR", Assert.Single(o.Backend.Ran).Id);
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    // ── the watch ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, false)]   // a left press outside
    [InlineData(false, true)]   // a right press outside
    public void A_left_or_right_press_outside_closes_it(bool left, bool right) => Sta(() =>
    {
        var o = Open();
        try
        {
            o.Sample = Away;
            o.Window.WatchTick();
            Assert.False(o.Closed, "moving the pointer outside does nothing");
            o.Sample = Away with { LeftDown = left, RightDown = right };
            o.Window.WatchTick();
            Assert.True(o.Closed);
            Assert.Equal("outside", o.Window.CloseReason);
            Assert.Empty(o.Backend.Ran);
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    /// <summary>
    /// The real reading: Windows is asked for the left, right and middle buttons and nothing else (no wheel,
    /// no other key), and a middle button that is down, or the wheel turning (which the reading cannot even
    /// see), leaves the window open.
    /// </summary>
    [Fact]
    public void The_real_reading_asks_only_for_three_buttons_and_the_middle_one_never_closes() => Sta(() =>
    {
        var asked = new List<int>();
        var middleDown = true;
        var o = Open();
        try
        {
            var hwnd = new WindowInteropHelper(o.Window).Handle;
            PointerSample? Read() => SearchItWindow.SampleNative(hwnd, IntPtr.Zero, key =>
            {
                asked.Add(key);
                return key == SearchItWindow.VK_MBUTTON && middleDown ? unchecked((short)0x8000) : (short)0;
            });

            var sample = Assert.NotNull(Read());
            Assert.Equal([SearchItWindow.VK_LBUTTON, SearchItWindow.VK_RBUTTON, SearchItWindow.VK_MBUTTON], asked);
            Assert.True(sample.MiddleDown);
            Assert.False(sample.LeftDown || sample.RightDown || sample.LeftPressedSince || sample.RightPressedSince);
            Assert.False(sample.OwnerInFront, "no owner: nothing to take the keyboard back from");

            // The window watches through that reading: a pan (middle down, held, released) with the pointer
            // outside. Whatever is in front on the test machine is taken out of it: this is about the buttons.
            o.Window.Sampler = () => Read() is { } read ? read with { PointerInside = false, PressOnWindow = false, HostInFront = true } : null;
            o.Window.RebaseWatch();
            for (var i = 0; i < 10; i++) o.Window.WatchTick();
            middleDown = false;
            for (var i = 0; i < 10; i++) o.Window.WatchTick();
            Assert.False(o.Closed, "the middle button and the wheel never close the window");
            Assert.All(asked, key => Assert.Contains(key, new[] { SearchItWindow.VK_LBUTTON, SearchItWindow.VK_RBUTTON, SearchItWindow.VK_MBUTTON }));

            Assert.Null(SearchItWindow.SampleNative(IntPtr.Zero, IntPtr.Zero));
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    [Fact]
    public void After_a_pan_and_a_press_inside_a_left_click_in_the_view_still_closes_it() => Sta(() =>
    {
        var o = Open();
        try
        {
            o.Sample = Away with { MiddleDown = true };
            for (var i = 0; i < 20; i++) o.Window.WatchTick();
            o.Sample = Away;
            o.Window.WatchTick();
            Assert.False(o.Closed);

            // A press inside (a drag of the window's edge), held while the pointer leaves, then released.
            o.Sample = Idle with { LeftDown = true };
            o.Window.WatchTick();
            o.Sample = Away with { LeftDown = true };
            o.Window.WatchTick();
            o.Sample = Away;
            o.Window.WatchTick();
            Assert.False(o.Closed);

            // And after all that a left click in the view still closes it (the case Deactivated alone misses).
            o.Sample = Away with { LeftDown = true };
            o.Window.WatchTick();
            Assert.True(o.Closed);
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    /// <summary>
    /// The reading says where the pointer is when it is taken, not where the button went down. A press the
    /// window's own mouse-down saw is the window's, even when the pointer is already outside at the first
    /// reading that sees the button down (a fast drag, a late timer); so is one made while it holds the capture.
    /// </summary>
    [Fact]
    public void A_press_on_the_window_read_with_the_pointer_already_outside_does_not_close() => Sta(() =>
    {
        var o = Open();
        try
        {
            Mouse2(o.Window.SearchBox, Mouse.PreviewMouseDownEvent, Environment.TickCount); // the window sees its own press
            o.Sample = Away with { LeftDown = true };
            o.Window.WatchTick();
            o.Window.WatchTick();
            Assert.False(o.Closed, "the press was on the window");
            o.Sample = Away;
            o.Window.WatchTick();

            // The capture says the same (a drag of the edge or the title bar, which raises no WPF mouse event).
            o.Sample = Away with { LeftDown = true, PressOnWindow = true };
            o.Window.WatchTick();
            Assert.False(o.Closed);
            o.Sample = Away;
            o.Window.WatchTick();

            // The claim does not outlive the press: the next press outside closes.
            o.Sample = Away with { LeftDown = true };
            o.Window.WatchTick();
            Assert.True(o.Closed);
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    /// <summary>After the tools are read the watch starts over: a click made during the read is not a click outside.</summary>
    [Fact]
    public void A_fresh_baseline_forgets_what_was_pressed_before() => Sta(() =>
    {
        var o = Open();
        try
        {
            o.Sample = Away with { LeftDown = true };
            o.Window.RebaseWatch();
            o.Window.WatchTick();
            Assert.False(o.Closed, "held since before the baseline");
            o.Sample = Away;
            o.Window.WatchTick();
            Assert.False(o.Closed);
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    /// <summary>
    /// A middle-button pan or orbit makes Revit the active window. When the button comes up with Revit's main
    /// window in front, the window takes the keyboard back: the box is focused again, text and caret as they were.
    /// </summary>
    [Fact]
    public void The_middle_button_coming_up_with_Revit_in_front_puts_the_keyboard_back_in_the_box() => Sta(() =>
    {
        var o = Open();
        try
        {
            var window = o.Window;
            window.SearchBox.Text = "wall";
            window.SearchBox.CaretIndex = 2;
            WindowCapture.DoEvents();
            window.FocusCard(1); // the keyboard was on a card when the pan started
            Assert.False(window.SearchBox.IsFocused);

            var revit = Away with { OwnerInFront = true };
            o.Sample = revit with { MiddleDown = true };
            window.WatchTick();
            Assert.False(window.SearchBox.IsFocused, "nothing happens while the button is down");
            o.Sample = revit;
            window.WatchTick();
            Assert.False(o.Closed);
            Assert.True(window.SearchBox.IsFocused, "the box has the focus again");
            Assert.Equal("wall", window.SearchBox.Text);
            Assert.Equal(2, window.SearchBox.CaretIndex);
            Assert.Equal(-1, window.ResultsList.SelectedIndex);
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    [Fact]
    public void The_keyboard_is_not_taken_back_unless_Revits_main_window_is_in_front() => Sta(() =>
    {
        var o = Open();
        try
        {
            var window = o.Window;
            window.FocusCard(1);
            // Another of Revit's windows in front: left alone.
            o.Sample = Away with { MiddleDown = true };
            window.WatchTick();
            o.Sample = Away;
            window.WatchTick();
            Assert.False(window.SearchBox.IsFocused);
            Assert.False(o.Closed);
            // Another application in front when the button comes up: the window closes; it does not grab focus.
            o.Sample = Away with { MiddleDown = true };
            window.WatchTick();
            o.Sample = Away with { HostInFront = false };
            window.WatchTick();
            Assert.True(o.Closed);
            Assert.Equal("outside", window.CloseReason);
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    [Fact]
    public void Another_application_in_front_closes_it() => Sta(() =>
    {
        var o = Open();
        try
        {
            o.Sample = Idle with { HostInFront = false };
            o.Window.WatchTick();
            Assert.True(o.Closed);
            // A tick after it closed is harmless.
            o.Window.WatchTick();
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    [Fact]
    public void The_watch_runs_on_its_own_timer_and_stops_when_the_window_closes() => Sta(() =>
    {
        var o = Open();
        var readings = 0;
        try
        {
            Assert.True(o.Window.IsWatching, "the timer runs from Loaded");
            o.Sample = Away;
            WindowCapture.DoEvents();
            o.Sample = Away with { LeftDown = true };
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!o.Closed && DateTime.UtcNow < until)
            {
                Thread.Sleep(20);
                WindowCapture.DoEvents();
                readings++;
            }
            Assert.True(o.Closed, $"the timer saw the press outside ({readings} waits)");
            Assert.False(o.Window.IsWatching, "the timer stops with the window");

            // However it closes.
            var other = Open();
            Assert.True(other.Window.IsWatching);
            other.Window.Close();
            Assert.False(other.Window.IsWatching);

            // And no reading is taken after that.
            var taken = 0;
            var third = Open();
            third.Window.Sampler = () =>
            {
                taken++;
                return Idle;
            };
            third.Window.Close();
            taken = 0;
            for (var i = 0; i < 5; i++)
            {
                Thread.Sleep(20);
                WindowCapture.DoEvents();
            }
            Assert.Equal(0, taken);
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    /// <summary>A reading that throws stops the watch after the first failure; the window stays, and Escape still closes it.</summary>
    [Fact]
    public void A_failing_reading_stops_the_watch_once_and_Escape_still_closes() => Sta(() =>
    {
        var o = Open();
        try
        {
            var calls = 0;
            o.Window.Sampler = () =>
            {
                calls++;
                throw new InvalidOperationException("the reading failed");
            };
            o.Window.WatchTick();
            Assert.Equal(1, calls);
            Assert.False(o.Window.IsWatching);
            Assert.False(o.Closed);
            o.Window.WatchTick();
            for (var i = 0; i < 4; i++)
            {
                Thread.Sleep(20);
                WindowCapture.DoEvents();
            }
            Assert.Equal(1, calls);
            Assert.True(Key2(o.Window.SearchBox, o.Window, Key.Escape));
            Assert.True(o.Closed);
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    // ── screenshots ──────────────────────────────────────────────────────

    private static string Name(string name, UiTheme theme) => $"{name}-{(theme == UiTheme.Dark ? "dark" : "light")}";

    private static void Capture(SearchItWindow window, UiTheme theme, string name)
    {
        WindowCapture.DoEvents();
        var content = (FrameworkElement)window.Content;
        var bitmap = WindowCapture.Content(window, new Size(content.ActualWidth, content.ActualHeight));
        Screenshots.Save(bitmap, Name(name, theme));
        // A short result list is a sparse picture (a few grey levels of text on one background): the bar is low,
        // and a blank image still has exactly one value.
        Assert.True(Screenshots.DistinctPixels(bitmap) > 8, $"{name} looks blank");
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Screenshots_of_the_results_the_chips_no_match_and_loading(UiTheme theme) => Sta(() =>
    {
        var tools = Sample.Concat(Many(40)).ToArray();
        var o = Open(new FakeBackend { Tools = tools });
        try
        {
            Capture(o.Window, theme, "search-it-all");

            o.Window.SearchBox.Text = "algn";
            Capture(o.Window, theme, "search-it-fuzzy");

            o.Window.SearchBox.Text = "tag";
            WindowCapture.DoEvents();
            o.Window.FocusCard(1);
            Capture(o.Window, theme, "search-it-highlight");

            o.Window.SearchBox.Text = "wall";
            o.Window.ViewModel.CommitChip();
            o.Window.SearchBox.Text = "arch";
            o.Window.ViewModel.CommitChip();
            o.Window.SearchBox.Text = "";
            Capture(o.Window, theme, "search-it-chips");
            Assert.Equal(["Wall: Architectural"], Names(o.Window.ViewModel));

            o.Window.SearchBox.Text = "zzzz";
            Capture(o.Window, theme, "search-it-no-match");
        }
        finally
        {
            CloseIfOpen(o);
        }

        var loading = new SearchItViewModel(new FakeBackend());
        loading.BeginLoading();
        var window = new SearchItWindow(loading, () => Idle);
        try
        {
            WindowCapture.ShowOffScreen(window);
            Capture(window, theme, "search-it-loading");
        }
        finally
        {
            window.Close();
        }
    }, theme);
}
