// rst-c's own (not vendored). SC-045 (issue #163): Health crashed Revit because a ControlTemplate's
// Loaded EventTrigger began a storyboard with Storyboard.TargetName="Arc" while the template was not
// built yet (the spinner was collapsed), so the name did not resolve and Storyboard threw inside
// ShowDialog. A template or style EventTrigger fires on the control whether its template parts exist or
// not, so a storyboard there must never name a part: put the trigger on the part itself (as
// Theme.xaml's BusySpinner and BusyIndicator now do). This lint keeps that true for every XAML file.

using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace RST.Tests.Theming;

public class XamlStoryboardTriggerLintTests
{
    private const string TargetNameAttribute = "Storyboard.TargetName";

    [Fact]
    public void NoTemplateOrStyleEventTrigger_BeginsAStoryboardOnANamedPart()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var files = Directory.EnumerateFiles(src, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(src, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        files.Should().Contain(f => f.EndsWith(Path.Combine("RST.UI", "Theme.xaml"), StringComparison.Ordinal),
            "the scan must reach Theme.xaml, where the busy styles live");

        var violations = files
            .SelectMany(f => Violations(XDocument.Load(f, LoadOptions.SetLineInfo))
                .Select(v => $"{Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/')}:{v}"))
            .ToList();

        violations.Should().BeEmpty(
            "an EventTrigger in ControlTemplate.Triggers, DataTemplate.Triggers or Style.Triggers fires before the " +
            "template's parts exist (a collapsed control is never templated), so Storyboard.TargetName throws " +
            "and takes Revit down (SC-045); move the trigger onto the named part and drop TargetName");
    }

    [Fact]
    public void TheLint_FindsTheShapeThatCrashedHealth()
    {
        // Theme.xaml's BusySpinner before the SC-045 fix, trimmed.
        const string old = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style x:Key="BusySpinner" TargetType="ProgressBar">
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="ProgressBar">
                      <Path x:Name="Arc" />
                      <ControlTemplate.Triggers>
                        <EventTrigger RoutedEvent="FrameworkElement.Loaded">
                          <BeginStoryboard>
                            <Storyboard RepeatBehavior="Forever">
                              <DoubleAnimation Storyboard.TargetName="Arc" From="0" To="360" />
                            </Storyboard>
                          </BeginStoryboard>
                        </EventTrigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
              <Style x:Key="Pulse" TargetType="Border">
                <Style.Triggers>
                  <EventTrigger RoutedEvent="FrameworkElement.Loaded">
                    <BeginStoryboard>
                      <Storyboard>
                        <DoubleAnimation Storyboard.TargetName="Glow" To="1" />
                      </Storyboard>
                    </BeginStoryboard>
                  </EventTrigger>
                </Style.Triggers>
              </Style>
            </ResourceDictionary>
            """;
        Violations(XDocument.Parse(old, LoadOptions.SetLineInfo)).Should().HaveCount(2);

        // The fix: the trigger on the part, the storyboard targeting the part it sits on.
        const string fixedXaml = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <ControlTemplate x:Key="T" TargetType="ProgressBar">
                <Path x:Name="Arc">
                  <Path.Triggers>
                    <EventTrigger RoutedEvent="FrameworkElement.Loaded">
                      <BeginStoryboard>
                        <Storyboard RepeatBehavior="Forever">
                          <DoubleAnimation Storyboard.TargetProperty="(UIElement.RenderTransform).(RotateTransform.Angle)" />
                        </Storyboard>
                      </BeginStoryboard>
                    </EventTrigger>
                  </Path.Triggers>
                </Path>
              </ControlTemplate>
            </ResourceDictionary>
            """;
        Violations(XDocument.Parse(fixedXaml, LoadOptions.SetLineInfo)).Should().BeEmpty();
    }

    /// <summary>"line N: …" for each storyboard element naming a target inside a template's or style's EventTrigger.</summary>
    private static IEnumerable<string> Violations(XDocument doc) =>
        doc.Descendants()
            .Where(e => IsTemplateOrStyleTriggers(e.Name.LocalName))
            .SelectMany(triggers => triggers.Elements().Where(t => t.Name.LocalName == "EventTrigger"))
            .SelectMany(trigger => trigger.Descendants()
                .Where(d => d.Attributes().Any(a => a.Name.LocalName == TargetNameAttribute))
                .Select(d => $"line {((System.Xml.IXmlLineInfo)d).LineNumber}: <{d.Name.LocalName} {TargetNameAttribute}=\"" +
                             $"{d.Attributes().First(a => a.Name.LocalName == TargetNameAttribute).Value}\"> in " +
                             $"<{trigger.Parent!.Name.LocalName}> EventTrigger {trigger.Attribute("RoutedEvent")?.Value}"));

    /// <summary>ControlTemplate.Triggers, DataTemplate.Triggers, HierarchicalDataTemplate.Triggers, Style.Triggers.</summary>
    private static bool IsTemplateOrStyleTriggers(string localName) =>
        localName.EndsWith("Template.Triggers", StringComparison.Ordinal) || localName == "Style.Triggers";

    private static bool IsBuildOutput(string src, string file)
    {
        var parts = Path.GetRelativePath(src, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Contains("obj") || parts.Contains("bin");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RST-C.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull($"repo root (RST-C.sln) must be discoverable from {AppContext.BaseDirectory}");
        return dir!.FullName;
    }
}
