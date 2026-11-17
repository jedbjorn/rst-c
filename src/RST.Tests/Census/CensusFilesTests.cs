// rst-c's own (not vendored): checks over the committed census tables (Feature #22, spec #34, AC3, AC4, AC5, AC8,
// and the file rules of AC9).
//
// census/<year>/full.tsv is the published table, exceptions.tsv the shipped list, contexts.tsv the context keys.
// They are written by tools/census from a run on the test VM and committed; these tests hold the rules a reviewer
// would otherwise check by hand.

using System.Reflection;
using FluentAssertions;
using RST.Core.Census;
using Xunit;

namespace RST.Tests.Census;

public sealed class CensusFilesTests
{
    public static readonly int[] ShippedYears = { 2026, 2027 };

    public static IEnumerable<object[]> Years => ShippedYears.Select(y => new object[] { y });

    /// <summary>The full table's columns (spec #34, "Full table columns").</summary>
    public static readonly string[] FullColumns =
    {
        "year", "build", "context", "id", "tab", "panel", "path", "label", "result", "tested", "opened", "teardown", "left",
        "gate", "ms", "run_date", "census_build",
    };

    public static readonly string[] ContextColumns = { "context", "kind", "category", "placement", "view_type", "view", "templates" };

    public static readonly string[] Results =
        { "executes", "greyed", "accepted-nothing", "not-drawn", "refused", "skipped", "error", "not-a-command" };

    // ---------- files ----------

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RST-C.sln"))) dir = dir.Parent;
        dir.Should().NotBeNull("the repo root (RST-C.sln) must be discoverable");
        return dir!.FullName;
    }

    internal static string CensusDir => Path.Combine(RepoRoot(), "census");

    internal static List<Dictionary<string, string>> Table(int year, string name, string[] columns)
    {
        var path = Path.Combine(CensusDir, year.ToString(System.Globalization.CultureInfo.InvariantCulture), name);
        File.Exists(path).Should().BeTrue($"census/{year}/{name} is committed");
        var lines = File.ReadAllLines(path);
        lines.Should().NotBeEmpty();
        lines[0].Split('\t').Should().Equal(columns, $"census/{year}/{name} has the spec's header");
        var rows = new List<Dictionary<string, string>>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            var f = lines[i].Split('\t');
            f.Length.Should().Be(columns.Length, $"census/{year}/{name} line {i + 1} has {columns.Length} fields");
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var c = 0; c < columns.Length; c++) row[columns[c]] = f[c];
            rows.Add(row);
        }
        return rows;
    }

    internal static List<Dictionary<string, string>> Full(int year) => Table(year, "full.tsv", FullColumns);
    internal static List<Dictionary<string, string>> Exceptions(int year) => Table(year, "exceptions.tsv", CensusIndex.Columns.ToArray());
    internal static List<Dictionary<string, string>> Contexts(int year) => Table(year, "contexts.tsv", ContextColumns);

    // ---------- shape ----------

    [Theory]
    [MemberData(nameof(Years))]
    public void FullTable_HasRowsAndOnlyKnownResults_AndNoErrors(int year)
    {
        var full = Full(year);
        full.Should().NotBeEmpty();
        full.Select(r => r["result"]).Distinct().Should().BeSubsetOf(Results);
        full.Should().NotContain(r => r["result"] == "error", "every error row is resolved before it is committed (AC1, AC2)");
        full.Should().OnlyContain(r => r["year"] == year.ToString(System.Globalization.CultureInfo.InvariantCulture));
        full.Select(r => r["tested"]).Distinct().Should().BeSubsetOf(new[] { "bind+cancel", "real", "gate", "" });
    }

    [Theory]
    [MemberData(nameof(Years))]
    public void EveryContextOfTheTable_IsListedInContextsTsv_AndViceVersa(int year)
    {
        var listed = Contexts(year).Select(r => r["context"]).ToList();
        listed.Should().OnlyHaveUniqueItems();
        Full(year).Select(r => r["context"]).Distinct().Should().BeEquivalentTo(listed);
        Exceptions(year).Select(r => r["context"]).Distinct().Should().BeSubsetOf(listed);
    }

    // ---------- AC4: only what the live checks can't see is shipped ----------

    [Theory]
    [MemberData(nameof(Years))]
    public void Exceptions_HoldOnlyNotRunnableAndNotDrawn_MatchingTheFullTable(int year)
    {
        var full = Full(year);
        var exceptions = Exceptions(year);
        exceptions.Select(r => r["mark"]).Distinct().Should().BeSubsetOf(new[] { "not-runnable", "not-drawn" });

        static string Key(IReadOnlyDictionary<string, string> r) => string.Join("\u001f", r["context"], r["id"], r["tab"], r["panel"], r["path"]);
        var byPlace = full.GroupBy(Key).ToDictionary(g => g.Key, g => g.First()["result"]);
        foreach (var e in exceptions)
        {
            byPlace.Should().ContainKey(Key(e), $"exceptions row {e["context"]} {e["id"]} has a full-table row");
            var result = byPlace[Key(e)];
            var expected = e["mark"] == "not-drawn" ? new[] { "not-drawn" } : new[] { "accepted-nothing", "refused" };
            result.Should().BeOneOf(expected, $"{e["id"]} in {e["context"]} is shipped as {e["mark"]}");
        }
        var shipped = exceptions.Select(Key).ToHashSet();
        full.Where(r => r["result"] is "accepted-nothing" or "refused" or "not-drawn")
            .Select(Key).Should().OnlyContain(k => shipped.Contains(k), "every not-runnable and not-drawn place ships");
    }

    [Fact]
    public void Level_IsGreyedInThe2026FloorPlan_AndExecutesInTheElevation()
    {
        var full = Full(2026);
        full.Where(r => r["context"] == CensusContext.Project("FloorPlan") && r["id"] == "ID_OBJECTS_LEVEL")
            .Should().NotBeEmpty().And.OnlyContain(r => r["result"] == "greyed" || r["result"] == "not-drawn");
        full.Where(r => r["context"] == CensusContext.Project("FloorPlan") && r["id"] == "ID_OBJECTS_LEVEL" && r["result"] == "greyed")
            .Should().NotBeEmpty();
        full.Where(r => r["context"] == CensusContext.Project("Elevation") && r["id"] == "ID_OBJECTS_LEVEL" && r["result"] == "executes")
            .Should().NotBeEmpty();
        Exceptions(2026).Should().NotContain(r => r["id"] == "ID_OBJECTS_LEVEL" && r["mark"] == "not-runnable");
    }

    // ---------- AC3: the WU1b–d oracle ----------

    [Fact]
    public void KnownExceptions_2026_AreFound()
    {
        var ex = Exceptions(2026);
        ex.Should().Contain(r => r["context"] == CensusContext.Project("FloorPlan") && r["id"] == "ID_RBS_HVAC_ZONE" && r["mark"] == "not-runnable");
        ex.Should().Contain(r => r["context"] == CensusContext.Family("OST_GenericModel", "OneLevelBased", "FloorPlan")
                                 && r["id"] == "ID_TRUSS_TOP_CHORD_CURVE" && r["mark"] == "not-runnable");
        foreach (var id in CensusOracle.NotDrawn2026)
            ex.Should().Contain(r => r["context"] == CensusContext.Project("FloorPlan") && r["id"] == id && r["mark"] == "not-drawn",
                $"{id} is not drawn in 2026 (WU1b)");
    }

    [Fact]
    public void Numbering_IsDrawnIn2027()
    {
        // AC3: Numbering is drawn on Manage › Settings in a 2027 project (it is not in 2026); in the family editor
        // Revit gates it off and does not draw it, so the check is the project contexts'
        Full(2027).Where(r => r["id"] == "ID_NUMBERING_OPTIONS" && r["context"].StartsWith("project/", StringComparison.Ordinal)).Should().NotBeEmpty()
            .And.NotContain(r => r["result"] == "not-drawn");
    }

    // ---------- AC5: third-party and skipped tools never mark ----------

    [Theory]
    [MemberData(nameof(Years))]
    public void NoThirdPartyRows(int year)
    {
        // Revit's built-in TwinmotionForRevit (vendor ADSK) is stock and keeps its rows (decision #113)
        var markers = new[] { "DiRoots", "BIM 42", "BIM42" };
        foreach (var name in new[] { "full.tsv", "exceptions.tsv" })
        {
            var text = File.ReadAllText(Path.Combine(CensusDir, year.ToString(System.Globalization.CultureInfo.InvariantCulture), name));
            foreach (var m in markers) text.Should().NotContainEquivalentOf(m, $"census/{year}/{name} has stock Revit only (decision #110)");
        }
    }

    [Theory]
    [MemberData(nameof(Years))]
    public void Dynamo_AndCarbonInsightsAnalyze_AreSkippedWithAReason_AndNeverShipped(int year)
    {
        var full = Full(year);
        var skippedIds = new[] { "ID_VISUAL_PROGRAMMING_DYNAMO", "CustomCtrl_%idCarbonAnalysis%idRunCarbonAnalysis" };
        foreach (var id in skippedIds)
        {
            var rows = full.Where(r => r["id"] == id).ToList();
            rows.Should().NotBeEmpty($"{id} is on the {year} ribbon");
            rows.Should().OnlyContain(r => r["result"] == "skipped", $"{id} is on the skip list");
        }
        // The full table has no reason column; a skipped row was never started, so its teardown field carries
        // "skip: <reason>" (tools/census/skip.tsv holds the same reason).
        full.Where(r => r["result"] == "skipped").Should().OnlyContain(r => r["teardown"].StartsWith("skip: ", StringComparison.Ordinal) && r["teardown"].Length > 6,
            "a skipped row carries its reason");
        Exceptions(year).Should().NotContain(r => skippedIds.Contains(r["id"]));
    }

    // ---------- AC8: the built RST.Core carries the committed lists (gzipped at build), byte for byte once decompressed ----------

    [Theory]
    [MemberData(nameof(Years))]
    public void BuiltRstCore_CarriesTheExceptionsFile_ByteIdentical(int year)
    {
        var asm = typeof(CensusIndex).Assembly;
        var name = CensusIndex.ResourceNameFor(year) + ".gz";
        asm.GetManifestResourceNames().Should().Contain(name);
        using var stream = CensusIndex.OpenResource(year)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var onDisk = File.ReadAllBytes(Path.Combine(CensusDir, year.ToString(System.Globalization.CultureInfo.InvariantCulture), "exceptions.tsv"));
        ms.ToArray().Should().Equal(onDisk);
    }

    [Fact]
    public void BuiltRstCore_CarriesOneResourcePerCommittedYear()
    {
        var dirs = Directory.GetDirectories(CensusDir).Select(Path.GetFileName)
            .Where(d => d!.Length == 4 && d.All(char.IsDigit) && File.Exists(Path.Combine(CensusDir, d, "exceptions.tsv")))
            .Select(d => int.Parse(d!, System.Globalization.CultureInfo.InvariantCulture)).OrderBy(y => y).ToList();
        CensusIndex.EmbeddedYears.Should().Equal(dirs);
        dirs.Should().Contain(ShippedYears);
    }

    // ---------- AC9 (file rules) and the add-in staying out of the release ----------

    [Fact]
    public void CensusFolder_HoldsOnlyTsvAndMarkdown()
    {
        Directory.EnumerateFiles(CensusDir, "*", SearchOption.AllDirectories)
            .Select(Path.GetExtension).Distinct().Should().BeSubsetOf(new[] { ".tsv", ".md" }, "no icons or screenshots are published (decision #98)");
    }

    [Fact]
    public void CensusAddin_IsNotInTheSolution_TheStagingOrTheInstallers()
    {
        var root = RepoRoot();
        File.ReadAllText(Path.Combine(root, "RST-C.sln")).Should().NotContain("RST.Census", "the release build is `dotnet build RST-C.sln` (build/stage.sh)");
        File.ReadAllText(Path.Combine(root, "build", "stage.sh")).Should().NotContain("RST.Census");
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "installer"), "*", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(Path.Combine(root, "installer-bundle"), "*", SearchOption.AllDirectories))
                     .Where(f => f.EndsWith(".wxs", StringComparison.Ordinal) || f.EndsWith(".wixproj", StringComparison.Ordinal) || f.EndsWith(".ps1", StringComparison.Ordinal)))
            File.ReadAllText(f).Should().NotContain("RST.Census", f);
        foreach (var csproj in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
                     .Where(p => !p.Contains("RST.Census", StringComparison.Ordinal)))
            File.ReadAllText(csproj).Should().NotContain("RST.Census.csproj", csproj);
    }
}
