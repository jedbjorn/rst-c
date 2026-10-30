// Adapted from splash-it tests/SplashIt.Core.Tests/Theming/UiPaletteTests.cs (splash-it commit ba86a71; originally gantt-it tests/GanttIt.Core.Tests/Theming/UiPaletteTests.cs, gantt-it commit a96d0a1), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); the XAML scan reads src/RST.UI and finds the repo by RST-C.sln; splash-it's SurfaceTokens_MatchTheTabTextBackdrops is left out (it needs SplashItDefaults); explicit Xunit usings; a test that every new rst-c style key has its contrast requirement, and one for the Health window's keys.

using System.Text.RegularExpressions;
using RST.Core.Theming;
using Xunit;
using Xunit.Abstractions;

namespace RST.Tests.Theming;

public class UiPaletteTests(ITestOutputHelper output)
{
    public static TheoryData<UiTheme> Themes => new() { UiTheme.Light, UiTheme.Dark };

    [Theory]
    [MemberData(nameof(Themes))]
    public void EveryPairing_MeetsItsContrastMinimum(UiTheme theme)
    {
        var palette = UiPalette.For(theme);
        var failures = new List<string>();
        foreach (var r in UiPalette.Requirements)
        {
            var ratio = UiColor.ContrastRatio(palette[r.Foreground], palette[r.Background]);
            output.WriteLine($"{theme,-5} {r.Foreground,-24} on {r.Background,-16} {ratio,5:0.00}:1 (min {r.Minimum}) {r.Use}");
            if (ratio < r.Minimum) failures.Add($"{r.Foreground} on {r.Background}: {ratio:0.00} < {r.Minimum}");
        }
        Assert.Empty(failures);
    }

    [Fact]
    public void BothPalettes_DefineTheSameKeys()
    {
        var light = UiPalette.For(UiTheme.Light).Keys.OrderBy(k => k, StringComparer.Ordinal);
        var dark = UiPalette.For(UiTheme.Dark).Keys.OrderBy(k => k, StringComparer.Ordinal);
        Assert.Equal(light, dark);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void TextAndBackgroundKeys_AreOpaque(UiTheme theme)
    {
        // Contrast is only meaningful against a solid background; only the chip outlines are translucent.
        var palette = UiPalette.For(theme);
        foreach (var r in UiPalette.Requirements)
        {
            Assert.True(palette[r.Foreground].IsOpaque, r.Foreground);
            Assert.True(palette[r.Background].IsOpaque, r.Background);
        }
    }

    [Fact]
    public void EveryBrushKeyTheXamlUses_IsInThePalette()
    {
        // A {StaticResource …Brush} missing from the palette only fails at runtime in Revit,
        // when the window's XAML loads. Keys a file defines itself (x:Key) are its own.
        var ui = Path.Combine(RepositoryRoot(), "src", "RST.UI");
        var files = Directory.GetFiles(ui, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToArray();
        Assert.NotEmpty(files);
        var palette = UiPalette.For(UiTheme.Light).Keys.ToHashSet(StringComparer.Ordinal);
        var missing = new List<string>();
        var used = 0;
        foreach (var file in files)
        {
            var xaml = File.ReadAllText(file);
            var local = Regex.Matches(xaml, @"x:Key=""(\w+)""").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(xaml, @"\{(?:Static|Dynamic)Resource (\w+Brush)\}"))
            {
                used++;
                var key = m.Groups[1].Value;
                if (!palette.Contains(key) && !local.Contains(key)) missing.Add($"{Path.GetFileName(file)}: {key}");
            }
        }
        Assert.True(used > 50, $"only {used} brush references found; is the scan looking at the right files?");
        Assert.Empty(missing);
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "RST-C.sln"))) return dir.FullName;
        throw new InvalidOperationException("RST-C.sln not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void LightPalette_KeepsGanttItsColors()
    {
        // The light theme is the palette gantt-it ships.
        var light = UiPalette.For(UiTheme.Light);
        Assert.Equal("#F4F5F7", light[UiPalette.Window].ToString());
        Assert.Equal("#FFFFFF", light[UiPalette.Surface].ToString());
        Assert.Equal("#1F2328", light[UiPalette.Text].ToString());
        Assert.Equal("#5E6672", light[UiPalette.MutedText].ToString());
        Assert.Equal("#2F6AD0", light[UiPalette.Accent].ToString());
        Assert.Equal("#C42B1C", light[UiPalette.Error].ToString());
    }

    [Fact]
    public void RstKeys_UnderOrOverText_HaveAContrastRequirement()
    {
        // rst-c's additions: every new key is checked, except the purely decorative outline and
        // the busy overlay's translucent wash (no text or control boundary is drawn on either).
        string[] decorative = [UiPalette.SuccessBorder, UiPalette.Scrim];
        string[] added = [UiPalette.Success, UiPalette.SuccessSoft, UiPalette.SuccessBorder, UiPalette.Scrim];
        var checkedKeys = UiPalette.RstRequirements.SelectMany(r => new[] { r.Foreground, r.Background }).ToHashSet(StringComparer.Ordinal);
        foreach (var key in added.Except(decorative))
            Assert.Contains(key, checkedKeys);
        Assert.All(UiPalette.RstRequirements, r => Assert.Contains(r, UiPalette.Requirements));
    }

    [Fact]
    public void HealthKeys_HaveAContrastRequirement()
    {
        // The Health window's additions (spec #16, WU2): the soft-warning severity color is
        // checked as text on every surface it is drawn on, and the requirements are in force.
        var checkedKeys = UiPalette.HealthRequirements.Select(r => r.Foreground).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(UiPalette.CautionText, checkedKeys);
        Assert.All(UiPalette.HealthRequirements, r => Assert.Contains(r, UiPalette.Requirements));
    }

    [Theory]
    [InlineData("#000000", "#FFFFFF", 21.0)]
    [InlineData("#FFFFFF", "#FFFFFF", 1.0)]
    [InlineData("#767676", "#FFFFFF", 4.54)]
    public void ContrastRatio_MatchesWcag(string foreground, string background, double expected)
    {
        Assert.Equal(expected, UiColor.ContrastRatio(UiColor.Parse(foreground), UiColor.Parse(background)), 2);
    }

    [Fact]
    public void Parse_ReadsAlpha_AndCompositesOverTheBackground()
    {
        var half = UiColor.Parse("#80000000");
        Assert.Equal(new UiColor(0x80, 0, 0, 0), half);
        Assert.Equal(UiColor.Parse("#7F7F7F"), half.Over(UiColor.Parse("#FFFFFF")));
        Assert.Throws<FormatException>(() => UiColor.Parse("#12345"));
    }
}
