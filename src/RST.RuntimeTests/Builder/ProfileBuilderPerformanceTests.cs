// rst-c's own (not vendored): AC7 (spec #16, WU4) and WU1 review note N2. The real Profile Builder
// window over an 823-command catalogue: a filter keystroke (the view-model's filter plus the WPF
// layout pass that follows it) takes under 100 ms at the median, and the height-bounded catalogue
// tree realizes far fewer TreeViewItems than there are commands (virtualization holds).
// A CI timing is noisy, so the budget is a median over a typed sequence, not a single run.

using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Threading;
using RST.Core.Theming;
using RST.RuntimeTests.Support;
using RST.UI.Builder;
using RST.UI.Theming;
using Xunit;
using Xunit.Abstractions;

namespace RST.RuntimeTests.Builder;

[Collection(WpfThemeCollection.Name)]
public class ProfileBuilderPerformanceTests(ITestOutputHelper output)
{
    /// <summary>Typing, deleting and retyping, the way a filter box is used.</summary>
    private static readonly string[] Keystrokes =
    [
        "w", "wa", "wal", "wall", "wal", "wa", "w", "",
        "d", "do", "doo", "door", "doo", "do", "d", "",
        "s", "se", "sec", "sect", "secti", "sectio", "section", "",
        "t", "to", "too", "tool", "tool 1", "tool 12", "", "id_", "id_pipe", "",
    ];

    [Fact]
    public void Filtering_823_commands_takes_under_100_ms_per_keystroke_and_the_tree_virtualizes() => Screenshots.RunSta(() =>
    {
        try
        {
            RstTheme.Apply(UiTheme.Light);
            var backend = new FakeBuilderBackend { Catalog = BuilderFixtures.Large() };
            Assert.Equal(823, backend.Catalog.Count);
            var model = new ProfileBuilderViewModel(backend, new FakeDialogs(), new Loader.ManualClock().Schedule, a => a());
            model.Initialize();
            Assert.Equal(823, model.CatalogCount);
            var window = new ProfileBuilderWindow(model);
            WindowCapture.ShowOffScreen(window);
            try
            {
                var tree = window.CatalogTreeView;
                Assert.True(tree.ActualHeight > 100 && tree.ActualHeight < 1000, $"the tree is height-bounded ({tree.ActualHeight:0} px)");

                // Open the native group so most of the 823 rows would show without virtualization.
                model.AllCatalogRoots[0].IsExpanded = true;
                WindowCapture.DoEvents();
                var realized = WindowCapture.Descendants<TreeViewItem>(tree).Count();
                output.WriteLine($"realized TreeViewItems with every group open: {realized} (of {823 + CountGroups(model)} rows)");
                Assert.True(realized < 150, $"virtualization: {realized} TreeViewItems realized for 823 commands");

                // Warm up once (JIT, first container generation), then time each keystroke.
                Type(model, window, "warm");
                Type(model, window, "");
                var times = new List<double>();
                foreach (var q in Keystrokes) times.Add(Type(model, window, q));
                times.Sort();
                var median = times[times.Count / 2];
                var summary = $"AC7 (spec #16, WU4): 823 commands; realized TreeViewItems with every group open: {realized}; "
                    + $"keystrokes: {times.Count}, median {median:0.0} ms, min {times[0]:0.0} ms, max {times[^1]:0.0} ms";
                output.WriteLine(summary);
                // Kept with the screenshots, so the CI artifact carries the measured numbers.
                System.IO.Directory.CreateDirectory(Screenshots.Folder);
                System.IO.File.WriteAllText(System.IO.Path.Combine(Screenshots.Folder, "builder-ac7-timing.txt"), summary + Environment.NewLine);
                Assert.True(median < 100, $"median filter keystroke {median:0.0} ms (budget 100 ms)");

                model.FilterText = "pipe";
                WindowCapture.DoEvents();
                var filtered = WindowCapture.Descendants<TreeViewItem>(tree).Count();
                Assert.True(filtered < 150, $"{filtered} TreeViewItems realized while filtering");
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });

    private static int CountGroups(ProfileBuilderViewModel model) =>
        model.AllCatalogRoots.SelectMany(r => r.Flatten()).Count(n => !n.IsCommand);

    /// <summary>One keystroke: the filter, then the layout pass WPF runs before the next frame.</summary>
    private static double Type(ProfileBuilderViewModel model, ProfileBuilderWindow window, string text)
    {
        var watch = Stopwatch.StartNew();
        model.FilterText = text;
        window.UpdateLayout();
        watch.Stop();
        WindowCapture.DoEvents();
        return watch.Elapsed.TotalMilliseconds;
    }
}
