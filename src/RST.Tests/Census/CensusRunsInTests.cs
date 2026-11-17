// rst-c's own (not vendored): the runs-in resource (Feature #21, spec #35, AC2a).
//
// Each build derives, per Revit year, the distinct (context, id) pairs whose census result is `executes` from the
// committed census/<year>/full.tsv and embeds them gzipped (RST.Core.csproj, RstCensusRunsIn). These tests rebuild
// the pairs from the committed table on their own and compare them with the embedded resource after decompression.

using FluentAssertions;
using RST.Core.Census;
using Xunit;

namespace RST.Tests.Census;

public sealed class CensusRunsInTests
{
    public static IEnumerable<object[]> Years => CensusFilesTests.Years;

    private static string Embedded(int year)
    {
        using var stream = CensusRunsIn.OpenResource(year);
        stream.Should().NotBeNull($"the runs-in list for {year} is embedded");
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    /// <summary>The pairs, rebuilt from the committed full table without the build's code.</summary>
    private static List<string> FromFullTable(int year) =>
        CensusFilesTests.Full(year)
            .Where(r => r["result"] == "executes")
            .Select(r => r["context"] + "\t" + r["id"])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    [Theory]
    [MemberData(nameof(Years))]
    public void The_embedded_resource_is_the_full_tables_executes_pairs(int year)
    {
        var expected = CensusRunsIn.Header + "\n" + string.Join("", FromFullTable(year).Select(p => p + "\n"));
        Embedded(year).Should().Be(expected, $"census/{year}/full.tsv's executes pairs, sorted, under the header");
    }

    [Fact]
    public void The_2026_list_holds_the_spec_count()
    {
        CensusRunsIn.For(2026).PairCount.Should().Be(17_938, "spec #35, Scope: 2026 has 17,938 pairs");
        CensusRunsIn.For(2027).PairCount.Should().Be(FromFullTable(2027).Count);
    }

    [Fact]
    public void The_resource_is_small()
    {
        foreach (var year in CensusFilesTests.ShippedYears)
        {
            using var gz = typeof(CensusRunsIn).Assembly.GetManifestResourceStream(CensusRunsIn.ResourceNameFor(year))!;
            gz.Length.Should().BeLessThan(120_000, "about 76 KB gzipped per year (spec #35)");
        }
    }

    [Fact]
    public void The_loaded_list_answers_from_the_tables()
    {
        var runsIn = CensusRunsIn.For(2026);
        runsIn.Runs("project/Elevation", "ID_OBJECTS_LEVEL").Should().BeTrue();
        runsIn.Runs("project/Section", "ID_OBJECTS_LEVEL").Should().BeTrue();
        runsIn.Runs("project/FloorPlan", "ID_OBJECTS_LEVEL").Should().BeFalse("Level is greyed in a floor plan");
        runsIn.ContextsOf("ID_OBJECTS_LEVEL").Where(c => c.StartsWith("project/", StringComparison.Ordinal))
            .Should().BeEquivalentTo("project/Elevation", "project/Section");
        runsIn.ContextsOf("ID_RBS_HVAC_ZONE").Should().BeEmpty("HVAC Zone runs in no context");
        runsIn.ContextsOf("ID_NOT_A_TOOL").Should().BeEmpty();
        runsIn.Runs(null, "ID_OBJECTS_LEVEL").Should().BeFalse();
        CensusRunsIn.For(2025).IsEmpty.Should().BeTrue("Revit 2025 has no census");
    }

    [Fact]
    public void Parse_skips_the_header_blank_and_malformed_lines_and_repeats()
    {
        var parsed = CensusRunsIn.Parse(2026, "context\tid\r\nproject/FloorPlan\tID_A\n\nproject/FloorPlan\tID_A\nproject/Section\tID_A\n"
            + "no-tab\nproject/FloorPlan\t\n\tID_B\nproject/FloorPlan\tID_B\textra\n");
        parsed.PairCount.Should().Be(2);
        parsed.ContextsOf("ID_A").Should().Equal("project/FloorPlan", "project/Section");
        parsed.ContextsOf("ID_B").Should().BeEmpty();
        CensusRunsIn.Parse(2026, null).IsEmpty.Should().BeTrue();
    }
}
