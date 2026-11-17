// rst-c's own (not vendored): the census context key of the open file and view (Feature #21, spec #35, AC1).
//
// Search-It builds the key from plain values the Engine reads (family or project, category, placement, view type) and
// resolves it against the year's contexts.tsv, which each build embeds from the committed file.

using FluentAssertions;
using RST.Core.Census;
using Xunit;

namespace RST.Tests.Census;

public sealed class CensusContextKeyTests
{
    public static IEnumerable<object[]> Years => CensusFilesTests.Years;

    private static string Committed(int year, string name) =>
        File.ReadAllText(Path.Combine(CensusFilesTests.CensusDir, year.ToString(System.Globalization.CultureInfo.InvariantCulture), name));

    [Theory]
    [MemberData(nameof(Years))]
    public void The_embedded_contexts_are_the_committed_file(int year)
    {
        using var stream = CensusContexts.OpenResource(year);
        stream.Should().NotBeNull($"contexts.tsv for {year} is embedded");
        using var reader = new StreamReader(stream!);
        reader.ReadToEnd().Should().Be(Committed(year, "contexts.tsv"));
        CensusContexts.For(year).Rows.Select(r => r.Context).Should().Equal(
            CensusFilesTests.Contexts(year).Select(r => r["context"]), "the loader keeps every context, in file order");
    }

    [Theory]
    [MemberData(nameof(Years))]
    public void Every_census_context_key_round_trips_from_its_plain_values(int year)
    {
        var contexts = CensusContexts.For(year);
        var rows = CensusFilesTests.Contexts(year);
        rows.Should().HaveCountGreaterThan(100);
        foreach (var row in rows)
        {
            var isFamily = row["kind"] == "family";
            var document = new CensusDocument(isFamily, isFamily ? row["category"] : null, isFamily ? row["placement"] : null, row["view_type"]);
            var built = isFamily
                ? CensusContext.Family(row["category"], row["placement"], row["view_type"])
                : CensusContext.Project(row["view_type"]);
            built.Should().Be(row["context"], "the key is made of the row's own plain values");
            var key = contexts.Resolve(document);
            key.Should().NotBeNull(row["context"]);
            key!.Key.Should().Be(row["context"]);
            key.IsFallback.Should().BeFalse();
            key.IsFamily.Should().Be(isFamily);
            key.ViewType.Should().Be(row["view_type"]);
        }
    }

    [Theory]
    [InlineData("AreaPlan", "project/FloorPlan")]
    [InlineData("EngineeringPlan", "project/FloorPlan")]
    [InlineData("Detail", "project/Section")]
    [InlineData("Walkthrough", "project/ThreeD")]
    [InlineData("PanelSchedule", "project/Schedule")]
    [InlineData("ColumnSchedule", "project/Schedule")]
    public void Each_fallback_row_maps_as_specified(string viewType, string expected)
    {
        foreach (var year in CensusFilesTests.ShippedYears)
        {
            var key = CensusContexts.For(year).Resolve(new CensusDocument(false, null, null, viewType));
            key.Should().NotBeNull();
            key!.Key.Should().Be(expected);
            key.Asked.Should().Be("project/" + viewType);
            key.IsFallback.Should().BeTrue();
            key.ToString().Should().Be($"{expected} (from {viewType})");
        }
        CensusContexts.ProjectFallback.Should().HaveCount(6, "the spec's table, and nothing more");
    }

    [Theory]
    [InlineData(false, null, null, "Rendering")]           // a view type the census did not run, with no fallback row
    [InlineData(false, null, null, "Report")]
    [InlineData(false, null, null, "")]                    // no active view
    [InlineData(false, null, null, null)]
    [InlineData(true, "OST_Nonsense", "OneLevelBased", "FloorPlan")]       // a category the census did not run
    [InlineData(true, "OST_GenericModel", "ViewBased", "FloorPlan")]       // a placement it did not run for that category
    [InlineData(true, "Invalid", "Invalid", "FloorPlan")]                  // the family's values could not be read
    [InlineData(true, "", "", "FloorPlan")]
    public void An_unknown_category_placement_or_view_gives_no_key(bool isFamily, string? category, string? placement, string? viewType)
    {
        foreach (var year in CensusFilesTests.ShippedYears)
            CensusContexts.For(year).Resolve(new CensusDocument(isFamily, category, placement, viewType)).Should().BeNull();
    }

    [Fact]
    public void A_family_view_the_census_did_not_run_uses_the_familys_first_context_in_file_order()
    {
        var contexts = CensusContexts.For(2026);
        // A generic model family in an elevation (Front): the census ran its floor plan and its 3D view.
        var key = contexts.Resolve(new CensusDocument(true, "OST_GenericModel", "OneLevelBased", "Elevation"));
        key.Should().NotBeNull();
        key!.Key.Should().Be("family/OST_GenericModel/OneLevelBased/FloorPlan");
        key.Asked.Should().Be("family/OST_GenericModel/OneLevelBased/Elevation");
        key.IsFallback.Should().BeTrue();
        // A lighting fixture family (wall based) in a floor plan: its first context is its elevation.
        contexts.Resolve(new CensusDocument(true, "OST_LightingFixtures", "OneLevelBasedHosted", "FloorPlan"))!.Key
            .Should().Be("family/OST_LightingFixtures/OneLevelBasedHosted/Elevation");
    }

    [Fact]
    public void A_project_template_uses_the_project_keys()
    {
        // A .rte opened directly is a project document (decision #105): IsFamily false, the project row.
        CensusContexts.For(2026).Resolve(new CensusDocument(false, "OST_GenericModel", "OneLevelBased", "FloorPlan"))!.Key
            .Should().Be("project/FloorPlan", "a project's key ignores family values");
    }

    [Fact]
    public void Same_file_contexts_are_every_project_view_or_the_familys_category_and_placement()
    {
        var contexts = CensusContexts.For(2026);
        var project = contexts.Resolve(new CensusDocument(false, null, null, "FloorPlan"))!;
        contexts.SameFile(project).Select(r => r.ViewType).Should().BeEquivalentTo(
            "FloorPlan", "CeilingPlan", "ThreeD", "Section", "Elevation", "DrawingSheet", "DraftingView", "Legend", "Schedule");
        var family = contexts.Resolve(new CensusDocument(true, "OST_LightingFixtures", "OneLevelBased", "FloorPlan"))!;
        contexts.SameFile(family).Select(r => r.Context).Should().Equal(
            "family/OST_LightingFixtures/OneLevelBased/FloorPlan", "family/OST_LightingFixtures/OneLevelBased/ThreeD");
    }

    [Fact]
    public void Parse_skips_the_header_blank_lines_and_rows_missing_a_key_part()
    {
        var parsed = CensusContexts.Parse(2026, string.Join("\r\n",
            string.Join("\t", CensusContexts.Columns),
            "project/FloorPlan\tproject\t\t\tFloorPlan\tLevel 1\tdefault.rte",
            "",
            "project/FloorPlan\tproject\t\t\tFloorPlan\tLevel 1\tdefault.rte",   // a repeat
            "\tproject\t\t\tSection\t\t",
            "project/Legend\t\t\t\tLegend\t\t",
            "family/OST_X/ViewBased/DrawingSheet\tfamily\tOST_X\tViewBased\tDrawingSheet\tSheet\tx.rft",
            "short\trow"));
        parsed.Rows.Select(r => r.Context).Should().Equal("project/FloorPlan", "family/OST_X/ViewBased/DrawingSheet");
        CensusContexts.Parse(2026, null).IsEmpty.Should().BeTrue();
        CensusContexts.Empty(2026).Resolve(new CensusDocument(false, null, null, "FloorPlan")).Should().BeNull();
    }

    [Fact]
    public void A_year_with_no_census_has_no_contexts() =>
        CensusContexts.For(2025).IsEmpty.Should().BeTrue("Revit 2025 has no census (spec #35, Out of Scope)");
}
