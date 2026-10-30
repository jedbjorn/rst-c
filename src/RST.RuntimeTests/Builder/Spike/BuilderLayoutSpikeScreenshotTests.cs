// F18 Builder UX Redesign, Phase 1 fidelity spike (spec #19). THROWAWAY, never merged: renders the
// layout-only spike window in light and dark, in each state, to f18-spike-<state>-<theme>.png in the
// screenshots folder CI uploads as RST-screenshots, for the FnB to approve before Phase 2.

using System.Windows;
using System.Windows.Threading;
using RST.Core.Theming;
using RST.RuntimeTests.Support;
using RST.UI.Builder.Spike;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.Builder.Spike;

[Collection(WpfThemeCollection.Name)]
public class BuilderLayoutSpikeScreenshotTests
{
    private static readonly Size BuilderSize = new(1450, 900);

    public static TheoryData<UiTheme> Themes => new() { UiTheme.Light, UiTheme.Dark };

    private static readonly (SpikeState State, string Slug)[] States =
    [
        (SpikeState.Default, "default"),
        (SpikeState.SettingsMenu, "settings-menu"),
        (SpikeState.ProfileMenu, "profile-menu"),
        (SpikeState.LoaderDefaults, "loader-defaults"),
        (SpikeState.JunkCleanup, "junk-cleanup"),
        (SpikeState.CreateTool, "create-tool"),
        (SpikeState.NewStack, "new-stack"),
    ];

    private static void Sta(Action action) => Screenshots.RunSta(() =>
    {
        try
        {
            action();
        }
        finally
        {
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_spike_renders_every_state(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var window = new BuilderLayoutSpikeWindow();
        WindowCapture.ShowOffScreen(window);
        try
        {
            foreach (var (state, slug) in States)
            {
                window.ShowState(state);
                WindowCapture.DoEvents();
                var bitmap = WindowCapture.Content(window, BuilderSize);
                Screenshots.Save(bitmap, $"f18-spike-{slug}-{theme.ToString().ToLowerInvariant()}");
                Assert.Equal(1450, bitmap.PixelWidth);
                Assert.Equal(900, bitmap.PixelHeight);
                Assert.True(Screenshots.DistinctPixels(bitmap) > 200, $"f18-spike-{slug} looks blank");
            }
        }
        finally
        {
            window.Close();
        }
    });
}
