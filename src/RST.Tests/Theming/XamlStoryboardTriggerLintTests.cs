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

    private static List<string> XamlFiles()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var files = Directory.EnumerateFiles(src, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(src, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        files.Should().Contain(f => f.EndsWith(Path.Combine("RST.UI", "Theme.xaml"), StringComparison.Ordinal),
            "the scan must reach Theme.xaml, where the busy styles live");
        return files;
    }

    private static List<string> Scan(Func<XDocument, IEnumerable<string>> rule) =>
        XamlFiles()
            .SelectMany(f => rule(XDocument.Load(f, LoadOptions.SetLineInfo))
                .Select(v => $"{Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/')}:{v}"))
            .ToList();

    [Fact]
    public void NoTemplateOrStyleEventTrigger_BeginsAStoryboardOnANamedPart()
    {
        var violations = Scan(Violations);

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

    [Fact]
    public void EveryTargetAndSourceNameInATemplate_NamesAnElementOfThatTemplate()
    {
        var violations = Scan(UnresolvedNames);

        violations.Should().BeEmpty(
            "a TargetName, SourceName or Storyboard.TargetName in a template resolves in that template's name scope; " +
            "a name the template does not define fails only at run time, on the UI thread inside Revit");
    }

    [Fact]
    public void TheNameCheck_FindsANameTheTemplateDoesNotDefine()
    {
        const string xaml = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <ControlTemplate x:Key="A" TargetType="Button">
                <Border x:Name="Bd">
                  <ContentPresenter />
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True">
                    <Setter TargetName="Bd" Property="Opacity" Value="0.5" />
                    <Setter TargetName="Chrome" Property="Opacity" Value="0.5" />
                  </Trigger>
                  <Trigger SourceName="Missing" Property="IsMouseOver" Value="True">
                    <Setter TargetName="Bd" Property="Opacity" Value="1" />
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
              <DataTemplate x:Key="B">
                <Grid>
                  <ContentControl>
                    <ContentControl.ContentTemplate>
                      <DataTemplate>
                        <Border Name="Inner" />
                      </DataTemplate>
                    </ContentControl.ContentTemplate>
                  </ContentControl>
                </Grid>
                <DataTemplate.Triggers>
                  <Trigger Property="IsEnabled" Value="False">
                    <Setter TargetName="Inner" Property="Opacity" Value="0.4" />
                  </Trigger>
                </DataTemplate.Triggers>
              </DataTemplate>
            </ResourceDictionary>
            """;

        // Chrome and Missing are not in A; Inner is in B's nested template, a scope of its own.
        UnresolvedNames(XDocument.Parse(xaml, LoadOptions.SetLineInfo)).Should().HaveCount(3)
            .And.Contain(v => v.Contains("\"Chrome\""))
            .And.Contain(v => v.Contains("\"Missing\""))
            .And.Contain(v => v.Contains("\"Inner\""));
    }

    private static readonly string[] NameReferences = ["TargetName", "SourceName", TargetNameAttribute];

    /// <summary>"line N: …" for each TargetName/SourceName/Storyboard.TargetName in a template that its template does not define.</summary>
    private static IEnumerable<string> UnresolvedNames(XDocument doc)
    {
        foreach (var template in doc.Descendants().Where(e => IsTemplate(e.Name.LocalName)))
        {
            var own = OwnScope(template).ToList();
            var names = own
                .SelectMany(e => e.Attributes().Where(a => a.Name.LocalName == "Name"))
                .Select(a => a.Value)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var e in own)
            {
                foreach (var a in e.Attributes().Where(a => NameReferences.Contains(a.Name.LocalName) && !names.Contains(a.Value)))
                {
                    yield return $"line {((System.Xml.IXmlLineInfo)e).LineNumber}: <{e.Name.LocalName} {a.Name.LocalName}=\"{a.Value}\"> " +
                                 $"names nothing in its <{template.Name.LocalName}>";
                }
            }
        }
    }

    /// <summary>The template's descendants outside any template nested in it (each template is its own name scope).</summary>
    private static IEnumerable<XElement> OwnScope(XElement template)
    {
        foreach (var child in template.Elements())
        {
            if (IsTemplate(child.Name.LocalName)) continue;
            yield return child;
            foreach (var deeper in OwnScope(child)) yield return deeper;
        }
    }

    /// <summary>ControlTemplate, DataTemplate, HierarchicalDataTemplate, ItemsPanelTemplate (not their property elements).</summary>
    private static bool IsTemplate(string localName) =>
        !localName.Contains('.') && localName.EndsWith("Template", StringComparison.Ordinal);

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
