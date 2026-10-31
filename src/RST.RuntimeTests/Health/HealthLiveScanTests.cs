// rst-c's own (not vendored): the real HealthScanner on the Windows CI runner (decision #33). It scans
// this machine (no Revit, no model), renders the Health window from the result in light and dark
// (health-live-<theme>.png) and writes every row and the snapshot's JSON to health-live.txt, so a
// change to how a section is read can be compared run against run.

using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RST.Core.Theming;
using RST.RuntimeTests.Support;
using RST.UI.Health;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.Health;

[Collection(WpfThemeCollection.Name)]
public class HealthLiveScanTests
{
    private sealed class NoTimer : IDisposable
    {
        public void Dispose()
        {
        }
    }

    [Fact]
    public void A_real_scan_fills_every_section_and_renders_in_both_themes()
    {
        var snap = HealthScanner.Capture();

        // What a Windows machine always has: the memory, the C: drive and the OS.
        Assert.True(snap.Ram.TotalMB > 0, "RAM total");
        Assert.True(snap.Disk.TotalGB > 0, "disk total");
        Assert.False(string.IsNullOrEmpty(snap.Os.Build), "OS build");
        Assert.False(string.IsNullOrEmpty(snap.Cpu.Name), "CPU name");

        var report = HealthReport.Build(snap, CultureInfo.GetCultureInfo("en-US"), TimeZoneInfo.Utc);
        var text = new StringBuilder();
        foreach (var section in report.Sections)
        {
            text.AppendLine("## " + section.Title);
            foreach (var row in section.Rows) text.AppendLine(row.ToString());
            text.AppendLine();
        }
        text.AppendLine("## snapshot");
        text.AppendLine(System.Text.Json.JsonSerializer.Serialize(snap, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Directory.CreateDirectory(Screenshots.Folder);
        File.WriteAllText(Path.Combine(Screenshots.Folder, "health-live.txt"), text.ToString());

        foreach (var theme in new[] { UiTheme.Light, UiTheme.Dark })
            Screenshots.RunSta(() => Render(snap, theme));
    }

    private static void Render(RST.Core.Health.HealthSnapshot snap, UiTheme theme)
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            RstTheme.Apply(theme);
            var vm = new HealthViewModel(new FakeHealthService { Snapshot = snap }, new FakeDialogs(), new HealthViewModelOptions
            {
                RunInBackground = work =>
                {
                    work();
                    return Task.CompletedTask;
                },
                Culture = culture,
                Zone = TimeZoneInfo.Utc,
                ToastSchedule = (_, _) => new NoTimer(),
            });
            vm.Load();
            foreach (var s in vm.Sections) s.IsExpanded = true;
            var window = new HealthWindow(vm);
            try
            {
                WindowCapture.ShowOffScreen(window);
                WindowCapture.DoEvents();
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(900, double.PositiveInfinity));
                var bitmap = WindowCapture.Content(window, new Size(900, Math.Max(800, Math.Ceiling(root.DesiredSize.Height))));
                Screenshots.Save(bitmap, "health-live-" + theme.ToString().ToLowerInvariant());
                Assert.True(Screenshots.DistinctPixels(bitmap) > 40, "health-live looks blank");
                Assert.Equal(4, WindowCapture.Descendants<Expander>(window).Count(e => e.IsExpanded));
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
    }
}
