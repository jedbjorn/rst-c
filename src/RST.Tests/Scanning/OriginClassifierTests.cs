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
