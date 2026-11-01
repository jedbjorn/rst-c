using FluentAssertions;
using RST.Core.Scanning;
using Xunit;

namespace RST.Tests.Scanning;

public sealed class OriginClassifierTests
{
    private const string AutodeskRoot = @"C:\Program Files\Autodesk";

    [Theory]
    [InlineData("Architecture")]
    [InlineData("Modify")]
    [InlineData("RST")]
    public void BuiltinTab_NoPublisher_IsNative(string tab)
    {
        OriginClassifier.Classify(tab, assemblyPath: null,
                                   programFilesAutodesk: AutodeskRoot)
            .Should().Be(CommandOrigin.Native);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(2025, false)]
    [InlineData(2026, false)]
    [InlineData(2027, true)]
    [InlineData(2028, true)]
    public void Concrete_is_native_only_from_2027(int? year, bool native)
    {
        BuiltinTabs.Contains("Concrete", year).Should().Be(native);
        OriginClassifier.Classify("Concrete", null, programFilesAutodesk: AutodeskRoot, revitYear: year)
            .Should().Be(native ? CommandOrigin.Native : CommandOrigin.Custom);
        OriginClassifier.ClassifyRibbonButton("Concrete", "ID_NUMBERING_REINF_PARTITIONS", year)
            .Should().Be(native ? CommandOrigin.Native : CommandOrigin.Unknown);
        OriginClassifier.ClassifyRibbonButton("Concrete", "CustomCtrl_%Concrete%Acme%Go", year)
            .Should().Be(CommandOrigin.Custom);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2025)]
    [InlineData(2026)]
    [InlineData(2027)]
    [InlineData(2028)]
    public void Existing_builtin_tabs_remain_native_in_every_year(int? year)
    {
        foreach (var tab in new[] { "Steel", "Architecture" })
        {
            BuiltinTabs.Contains(tab, year).Should().BeTrue();
            OriginClassifier.Classify(tab, null, revitYear: year).Should().Be(CommandOrigin.Native);
            OriginClassifier.ClassifyRibbonButton(tab, "ID_TOOL", year).Should().Be(CommandOrigin.Native);
            RST.Core.Loader.RequiredAddinRules.IsNativeTab(tab, year).Should().BeTrue();
            RST.Core.Activation.RequiredAddinMatch.IsAddinRequirement(
                new RST.Core.Profiles.RequiredAddin { TabName = tab, AddinFile = "Acme.addin" }, year).Should().BeFalse();
        }
    }

    [Fact]
    public void NonAutodeskPublisher_IsThirdParty_EvenOnBuiltinTab()
    {
        OriginClassifier.Classify(
            tabName: "Architecture",
            assemblyPath: null,
            publisher: "Acme Corp",
            programFilesAutodesk: AutodeskRoot)
            .Should().Be(CommandOrigin.ThirdParty);
    }

    [Fact]
    public void AutodeskPublisher_IsAutodesk()
    {
        OriginClassifier.Classify(
            tabName: "Some Add-in",
            assemblyPath: null,
            publisher: "Autodesk, Inc.",
            programFilesAutodesk: AutodeskRoot)
            .Should().Be(CommandOrigin.Autodesk);
    }

    [Fact]
    public void DllUnderAutodeskRoot_NoPublisher_IsAutodesk()
    {
        OriginClassifier.Classify(
            tabName: "Some Add-in",
            assemblyPath: @"C:\Program Files\Autodesk\Revit 2024\Foo.dll",
            programFilesAutodesk: AutodeskRoot)
            .Should().Be(CommandOrigin.Autodesk);
    }

    [Fact]
    public void NoSignals_FallsThroughToCustom()
    {
        OriginClassifier.Classify(
            tabName: "Acme",
            assemblyPath: @"C:\Vendor\Acme\Tools.dll",
            programFilesAutodesk: AutodeskRoot)
            .Should().Be(CommandOrigin.Custom);
    }

    [Fact]
    public void DllPath_ForwardSlashes_StillMatchesAutodeskRoot()
    {
        OriginClassifier.Classify(
            tabName: null,
            assemblyPath: "C:/Program Files/Autodesk/Revit 2025/Foo.dll",
            programFilesAutodesk: AutodeskRoot)
            .Should().Be(CommandOrigin.Autodesk);
    }
}
