// rst-c's own (not vendored). Search-It (v3.4): the view-model's flows with a fake backend, and the
// real window: modeless and owned (no taskbar entry, owner set, Show returns with it open), eight
// result cards at the default size, resizable but never remembered, the busy overlay drawn before the
// tools are read, Enter makes a chip and never runs a tool, Escape closes, a left or right press
// outside closes while the middle button and the wheel do not, another application in front closes,
// and a click on a result closes the window first and then runs the tool. Screenshots: search-it-*.

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
        new("ID_ANNOTATIONS_DIMENSION_ALIGNED", "Aligned Dimension", "Annotate", "Dimension", 1),
        new("ID_OBJECTS_WALL", "Wall: Architectural", "Architecture", "Build", 2),
        new("ID_OBJECTS_STRUCTURAL_WALL", "Wall: Structural", "Structure", "Structure"),
        new("ID_OBJECTS_CURTAIN_WALL", "Curtain Wall", "Architecture", "Build"),
        new("ID_OBJECTS_DOOR", "Door", "Architecture", "Build"),
        new("ID_OBJECTS_WINDOW", "Window", "Architecture", "Build"),
        new("ID_OBJECTS_FLOOR", "Floor: Architectural", "Architecture", "Build"),
        new("ID_OBJECTS_ROOF", "Roof by Footprint", "Architecture", "Build"),
        new("ID_VIEW_NEW_SECTION", "Section", "View", "Create"),
        new("ID_VIEW_NEW_3DVIEW", "Default 3D View", "View", "Create"),
        new("ID_EDIT_MOVE", "Move", "Modify", "Modify"),
        new("CustomCtrl_%CustomCtrl_%Add-Ins%Kinship%Open", "Open Library", "Add-Ins", "Kinship"),
    ];

    private static SearchItViewModel Loaded(FakeBackend backend)
    {
        var vm = new SearchItViewModel(backend);
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
        Assert.Equal("13 tools", vm.StatusText);

        vm.Text = "algn";
        Assert.Equal("Align", Names(vm).First());
        vm.Text = "wall";
        Assert.Equal(["Wall: Structural", "Wall: Architectural", "Curtain Wall"], Names(vm));
        Assert.Equal("3 of 13 tools", vm.StatusText);
        Assert.False(vm.IsEmpty);

        vm.Text = "zzzz";
        Assert.Empty(vm.Results);
        Assert.True(vm.IsEmpty);
        Assert.Equal("No tools match.", vm.EmptyText);
    });

    [Fact]
    public void A_result_card_shows_the_name_and_where_the_tool_lives() => Sta(() =>
    {
        var vm = Loaded(new FakeBackend());
        vm.Text = "wall: arch";
        var card = Assert.Single(vm.Results);
        Assert.Equal("Wall: Architectural", card.Name);
        Assert.Equal("Architecture › Build", card.Location);
        Assert.True(card.HasMore);
        Assert.Equal("+2 more places", card.MoreText);
        vm.Text = "align";
        Assert.False(vm.Results[0].HasMore);
        Assert.Equal("Modify › Modify", vm.Results[0].Location);
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
        Assert.Empty(backend.Ran); // Enter never runs a tool

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

    [Fact]
    public void A_click_closes_first_and_then_runs_the_tool_once() => Sta(() =>
    {
        var order = new List<string>();
        var backend = new FakeBackend { OnRun = () => order.Add("run") };
        var vm = Loaded(backend);
        vm.RequestClose += (_, _) => order.Add("close");
        vm.Text = "door";
        var card = vm.Results[0];
        card.RunCommand.Execute(card);
        card.RunCommand.Execute(card); // a double click runs it once
        Assert.Equal(["close", "run"], order);
        Assert.Equal("ID_OBJECTS_DOOR", Assert.Single(backend.Ran).Id);
        Assert.Equal("ID_OBJECTS_DOOR", vm.Chosen?.Id);
    });

    [Fact]
    public void While_the_tools_load_the_window_is_busy_and_what_was_typed_is_kept() => Sta(() =>
    {
        var backend = new FakeBackend();
        var vm = new SearchItViewModel(backend);
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

    private static readonly PointerSample Idle = new(false, false, false, false, PointerInside: true, HostInFront: true);

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

    private static bool Press(UIElement source, Window w, Key key, RoutedEvent? routed = null)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w)!, 0, key) { RoutedEvent = routed ?? Keyboard.KeyDownEvent };
        source.RaiseEvent(args);
        WindowCapture.DoEvents();
        return args.Handled;
    }

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

    /// <summary>
    /// The launcher shows it modeless (Show returns with the window open and no modal loop running),
    /// owned by the handle it was given, and a second call focuses the open window instead of opening another.
    /// </summary>
    [Fact]
    public void The_launcher_shows_it_modeless_and_owned_and_only_once() => Sta(() =>
    {
        var owner = new Window { Width = 300, Height = 200, Title = "Owner" };
        WindowCapture.ShowOffScreen(owner);
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
            Assert.NotSame(window, SearchItLauncher.Current);
        }
        finally
        {
            SearchItLauncher.Current?.Close();
            owner.Close();
        }
    });

    /// <summary>The busy overlay is on screen before the tools are read: a cold catalogue build never freezes a blank window.</summary>
    [Fact]
    public void The_launcher_draws_the_busy_overlay_before_it_reads_the_tools() => Sta(() =>
    {
        var backend = new FakeBackend();
        bool? visible = null, busy = null, loaded = null;
        backend.OnLoad = () =>
        {
            var window = SearchItLauncher.Current;
            visible = window?.IsVisible;
            loaded = window?.IsLoaded;
            busy = window?.ViewModel.Busy.IsBusy;
        };
        try
        {
            SearchItLauncher.Show(IntPtr.Zero, backend, () => Idle);
            Assert.True(visible, "the window is shown before the tools are read");
            Assert.True(loaded, "and laid out and drawn");
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

    [Fact]
    public void Eight_result_cards_show_by_default_a_resize_is_not_remembered() => Sta(() =>
    {
        var o = Open(new FakeBackend { Tools = Many(60).ToArray() });
        try
        {
            WindowCapture.DoEvents();
            var list = o.Window.ResultsList;
            Near(SearchItLayout.VisibleRows * SearchItLayout.RowHeight, list.ActualHeight);
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

    private static int FullyVisibleCards(ListBox list) =>
        WindowCapture.Descendants<ListBoxItem>(list).Count(item =>
        {
            if (!item.IsVisible) return false;
            var top = item.TransformToAncestor(list).Transform(new Point(0, 0)).Y;
            return top >= -0.5 && top + item.ActualHeight <= list.ActualHeight + 0.5;
        });

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
            Assert.True(Press(box, o.Window, Key.Enter, Keyboard.PreviewKeyDownEvent));
            Assert.Equal(["wall"], o.Window.ViewModel.Chips.Select(c => c.Text));
            Assert.Equal("", box.Text);
            Assert.Empty(o.Backend.Ran);
            Assert.False(o.Closed, "Enter keeps the window open");
            Assert.Equal(Visibility.Visible, o.Window.ChipList.Visibility);
            var remove = WindowCapture.Descendants<Button>(o.Window.ChipList).Single();
            Assert.NotNull(remove.Command);

            Assert.True(Press(box, o.Window, Key.Back, Keyboard.PreviewKeyDownEvent));
            Assert.Empty(o.Window.ViewModel.Chips);
            Assert.Equal(Visibility.Collapsed, o.Window.ChipList.Visibility);
            // With text in the box Backspace is the box's own.
            box.Text = "do";
            o.Window.ViewModel.Text = "do";
            Assert.False(Press(box, o.Window, Key.Back, Keyboard.PreviewKeyDownEvent));
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
            Assert.True(Press(o.Window.SearchBox, o.Window, Key.Escape));
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
            WindowCapture.DoEvents();
            Assert.True(o.Closed);
            Assert.True(closedWhenRun, "the window is gone before the tool is posted");
            Assert.Equal("chosen", o.Window.CloseReason);
            Assert.Equal("ID_OBJECTS_DOOR", Assert.Single(o.Backend.Ran).Id);
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    [Theory]
    [InlineData(true, false)]   // a left press outside
    [InlineData(false, true)]   // a right press outside
    public void A_left_or_right_press_outside_closes_it(bool left, bool right) => Sta(() =>
    {
        var o = Open();
        try
        {
            o.Sample = Idle with { PointerInside = false };
            o.Window.WatchTick();
            Assert.False(o.Closed, "moving the pointer outside does nothing");
            o.Sample = new PointerSample(left, false, right, false, PointerInside: false, HostInFront: true);
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
    /// Panning, orbiting and zooming the view: the middle button and the wheel are not among the things the
    /// watch reads, so to it they are idle readings with the pointer outside, however many there are, and
    /// Revit being the active window (a pan activates it) changes nothing.
    /// </summary>
    [Fact]
    public void Middle_button_and_wheel_outside_leave_it_open_and_so_does_a_press_inside() => Sta(() =>
    {
        var o = Open();
        try
        {
            o.Sample = Idle with { PointerInside = false };
            for (var i = 0; i < 20; i++) o.Window.WatchTick();
            Assert.False(o.Closed);

            // A press inside (a drag of the window's edge), held while the pointer leaves, then released.
            o.Sample = Idle with { LeftDown = true };
            o.Window.WatchTick();
            o.Sample = Idle with { LeftDown = true, PointerInside = false };
            o.Window.WatchTick();
            o.Sample = Idle with { PointerInside = false };
            o.Window.WatchTick();
            Assert.False(o.Closed);

            // And after all that a left click in the view still closes it (the case Deactivated alone misses).
            o.Sample = Idle with { LeftDown = true, PointerInside = false };
            o.Window.WatchTick();
            Assert.True(o.Closed);
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
    public void The_watch_runs_on_its_own_timer_and_stops_with_the_window() => Sta(() =>
    {
        var o = Open();
        var readings = 0;
        try
        {
            o.Sample = Idle with { PointerInside = false };
            WindowCapture.DoEvents();
            o.Sample = new PointerSample(true, false, false, false, PointerInside: false, HostInFront: true);
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!o.Closed && DateTime.UtcNow < until)
            {
                Thread.Sleep(20);
                WindowCapture.DoEvents();
                readings++;
            }
            Assert.True(o.Closed, $"the timer saw the press outside ({readings} waits)");
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
