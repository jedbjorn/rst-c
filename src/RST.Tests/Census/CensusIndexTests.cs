// rst-c's own (not vendored): the census loader (Feature #22, spec #34, AC7).
//
// RST.Core.Census.CensusIndex reads the exceptions list each RST build embeds from census/<year>/exceptions.tsv.
// These tests use fixture text for the parsing and matching rules, and the embedded resources for "what ships".

using FluentAssertions;
using RST.Core.Census;
using Xunit;

namespace RST.Tests.Census;

public sealed class CensusIndexTests
{
    private const string Header = "context\tid\ttab\tpanel\tpath\tmark\treason";

    private static readonly string Fixture = string.Join("\n",
        Header,
        "project/FloorPlan\tID_RBS_HVAC_ZONE\tAnalyze\tSpaces & Zones\t\tnot-runnable\tjournal: cannot be invoked in this context",
        "project/FloorPlan\tID_RBS_HVAC_ZONE\tSystems\tHVAC\t/RibbonSplitButton(ID_X_RibbonListButton)\tnot-runnable\tjournal: cannot be invoked in this context",
        "project/FloorPlan\tID_INPLACE_WALL\tArchitecture\tBuild\t/RibbonSplitButton(ID_OBJECTS_WALL_RibbonListButton)\tnot-drawn\tno drawn control at this place",
        "project/FloorPlan\tID_INPLACE_WALL\tStructure\tStructure\t/RibbonFoldPanel/RibbonSplitButton(ID_OBJECTS_WALL_RibbonListButton)\tnot-drawn\t",
        "family/OST_GenericModel/OneLevelBased/FloorPlan\tID_TRUSS_TOP_CHORD_CURVE\tHome_Family\tModel\t\tnot-runnable\taccepted, nothing started",
        "# a comment line",
        "",
        "project/FloorPlan\tID_BAD_MARK\tArchitecture\tBuild\t\tgreyed\tnot a shipped mark",
        "project/FloorPlan\tID_TOO_FEW\tArchitecture\tBuild\tnot-runnable",
        "project/FloorPlan\tID_TOO_MANY\tArchitecture\tBuild\t\tnot-runnable\treason\textra",
        "\tID_NO_CONTEXT\tArchitecture\tBuild\t\tnot-runnable\t",
        "project/FloorPlan\t\tArchitecture\tBuild\t\tnot-runnable\t",
        "project/FloorPlan\tID_NO_TAB\t\tBuild\t\tnot-drawn\t");

    private static CensusIndex Index() => CensusIndex.Parse(2026, Fixture);

    [Fact]
    public void Parse_KeepsWellFormedRows_AndCountsTheMalformedOnes()
    {
        var index = Index();
        index.Rows.Should().HaveCount(5);
        index.MalformedRows.Should().Be(6, "bad mark, too few fields, too many fields, empty context, empty id, empty tab");
        index.Year.Should().Be(2026);
    }

    [Fact]
    public void Parse_NeverThrows_OnGarbage()
    {
        var act = () => CensusIndex.Parse(2026, "\u0000\t\t\n\r\n\t\tnot-drawn\n" + new string('\t', 50));
        act.Should().NotThrow();
        CensusIndex.Parse(2026, null).IsEmpty.Should().BeTrue();
        CensusIndex.Parse(2026, "").IsEmpty.Should().BeTrue();
        CensusIndex.Parse(2026, Header).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Parse_AcceptsCrLfAndAMissingReason()
    {
        var index = CensusIndex.Parse(2027, Header + "\r\nproject/Section\tID_A\tView\tCreate\t\tnot-drawn\r\n");
        index.Rows.Should().ContainSingle();
        index.Rows[0].Reason.Should().BeEmpty();
        index.MalformedRows.Should().Be(0);
    }

    [Fact]
    public void Mark_NotRunnable_HoldsForEveryPlaceOfTheIdInThatContext()
    {
        var index = Index();
        var anyPlace = index.Mark("project/FloorPlan", "ID_RBS_HVAC_ZONE", new CensusPlace("Analyze", "Spaces & Zones"));
        anyPlace.Kind.Should().Be(CensusMarkKind.NotRunnable);
        anyPlace.Reason.Should().Contain("cannot be invoked");
        index.Mark("project/FloorPlan", "ID_RBS_HVAC_ZONE", new CensusPlace("Somewhere", "Else", "/x")).Kind
            .Should().Be(CensusMarkKind.NotRunnable, "whether a tool starts does not depend on the place clicked");
        index.Mark("family/OST_GenericModel/OneLevelBased/FloorPlan", "ID_TRUSS_TOP_CHORD_CURVE", default).Kind
            .Should().Be(CensusMarkKind.NotRunnable);
    }

    [Fact]
    public void Mark_NotDrawn_HoldsOnlyForThatPlace()
    {
        var index = Index();
        index.Mark("project/FloorPlan", "ID_INPLACE_WALL", new CensusPlace("Architecture", "Build", "/RibbonSplitButton(ID_OBJECTS_WALL_RibbonListButton)"))
            .Kind.Should().Be(CensusMarkKind.NotDrawn);
        index.Mark("project/FloorPlan", "ID_INPLACE_WALL", new CensusPlace("Architecture", "Build"))
            .Kind.Should().Be(CensusMarkKind.None, "not-drawn needs the exact place: a hidden copy can share the tab and panel with a drawn one");
        index.Mark("project/FloorPlan", "ID_INPLACE_WALL", new CensusPlace("Architecture", "Build", "/elsewhere"))
            .Kind.Should().Be(CensusMarkKind.None);
        index.Mark("project/FloorPlan", "ID_INPLACE_WALL", new CensusPlace("Massing", "Build"))
            .Kind.Should().Be(CensusMarkKind.None);
    }

    [Fact]
    public void Mark_IsNone_ForUnknownContextIdOrEmptyArguments()
    {
        var index = Index();
        index.Mark("project/Legend", "ID_RBS_HVAC_ZONE", default).Should().Be(CensusMark.None);
        index.Mark("project/FloorPlan", "ID_OBJECTS_WALL", default).Should().Be(CensusMark.None);
        index.Mark(null, "ID_RBS_HVAC_ZONE", default).Should().Be(CensusMark.None);
        index.Mark("project/FloorPlan", null, default).Should().Be(CensusMark.None);
        index.Mark("project/floorplan", "ID_RBS_HVAC_ZONE", default).Should().Be(CensusMark.None, "keys are ordinal");
    }

    [Fact]
    public void For_AnUnknownYear_IsEmpty()
    {
        var index = CensusIndex.For(1999);
        index.IsEmpty.Should().BeTrue();
        index.Mark(CensusContext.Project("FloorPlan"), "ID_RBS_HVAC_ZONE", default).Should().Be(CensusMark.None);
        CensusIndex.Empty(2030).Mark("x", "y", default).Should().Be(CensusMark.None);
    }

    [Fact]
    public void ParseMark_KnowsOnlyTheTwoShippedMarks()
    {
        CensusIndex.ParseMark("not-runnable").Should().Be(CensusMarkKind.NotRunnable);
        CensusIndex.ParseMark(" NOT-DRAWN ").Should().Be(CensusMarkKind.NotDrawn);
        foreach (var other in new[] { "greyed", "executes", "skipped", "not-a-command", "error", "accepted-nothing", "refused", "", null })
            CensusIndex.ParseMark(other).Should().Be(CensusMarkKind.None);
    }

    [Fact]
    public void ContextKeys_AreBuiltFromRevitEnumNames()
    {
        CensusContext.Project("FloorPlan").Should().Be("project/FloorPlan");
        CensusContext.Family("OST_GenericModel", "OneLevelBased", "ThreeD").Should().Be("family/OST_GenericModel/OneLevelBased/ThreeD");
        CensusContext.Family("", "WorkPlaneBased", "FloorPlan").Should().Be("family/Invalid/WorkPlaneBased/FloorPlan");
        CensusContext.DocumentPart("family/OST_GenericModel/OneLevelBased/ThreeD").Should().Be("family/OST_GenericModel/OneLevelBased");
        CensusContext.DocumentPart("project/Section").Should().Be("project");
    }

    // ---- what ships (the embedded resources) ----

    [Fact]
    public void EmbeddedYears_Are2026And2027()
    {
        CensusIndex.EmbeddedYears.Should().Contain(new[] { 2026, 2027 });
    }

    [Theory]
    [InlineData(2026)]
    [InlineData(2027)]
    public void EmbeddedIndex_LoadsWithNoMalformedRows(int year)
    {
        var index = CensusIndex.For(year);
        index.Year.Should().Be(year);
        index.MalformedRows.Should().Be(0);
        index.IsEmpty.Should().BeFalse($"census/{year}/exceptions.tsv ships with rows");
    }

    [Fact]
    public void EmbeddedIndex_2026_MarksTheKnownExceptions()
    {
        // spec #34 AC3, through the loader (the committed tables are checked in CensusFilesTests).
        var index = CensusIndex.For(2026);
        index.Mark(CensusContext.Project("FloorPlan"), "ID_RBS_HVAC_ZONE", default).Kind.Should().Be(CensusMarkKind.NotRunnable);
        index.Mark(CensusContext.Family("OST_GenericModel", "OneLevelBased", "FloorPlan"), "ID_TRUSS_TOP_CHORD_CURVE", default).Kind
            .Should().Be(CensusMarkKind.NotRunnable);
        foreach (var id in CensusOracle.NotDrawn2026)
        {
            var row = index.Rows.FirstOrDefault(r => r.Context == CensusContext.Project("FloorPlan") && r.Id == id && r.Mark == CensusMarkKind.NotDrawn);
            row.Should().NotBeNull($"{id} is not drawn in 2026 (WU1b)");
            index.Mark(row!.Context, id, new CensusPlace(row.TabId, row.Panel, row.Path)).Kind.Should().Be(CensusMarkKind.NotDrawn);
        }
    }

    [Fact]
    public void EmbeddedIndex_2027_DoesNotDropNumbering()
    {
        var index = CensusIndex.For(2027);
        index.Rows.Should().NotContain(r => r.Id == "ID_NUMBERING_OPTIONS" && r.Mark == CensusMarkKind.NotDrawn && r.Context.StartsWith("project/"),
            "Numbering is drawn in a 2027 project (WU1c)");
    }
}

/// <summary>The WU1b–d oracle (spec #34 AC3).</summary>
internal static class CensusOracle
{
    /// <summary>In-Place Wall, Building Pad, Site Settings launcher, Numbering, Stair by Sketch (fold-panel place).</summary>
    public static readonly string[] NotDrawn2026 =
        { "ID_INPLACE_WALL", "ID_SITE_BUILDINGPAD", "ID_SITE_SETTINGS", "ID_NUMBERING_OPTIONS", "ID_OBJECTS_STAIRS_LEGACY" };
}
