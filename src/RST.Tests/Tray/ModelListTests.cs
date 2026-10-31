// ModelListTests.cs — spec #25 ACs 1 and 2 (F19 #25 WU1): the tray Models list logic over
// synthetic index rows. Visible set per chip and Revit-version filter, grouping by major and
// minor (including "unknown minor"), sort order, every aggregate card value, the model card's
// data, empty states, the detail's starting range, all-time search, and clamping of
// implausible index values. Rows are built by spec #24's own index builder
// (RollupEditor.BuildIndexRow) from daily rollup rows, so the fixtures have the real row shape.

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using RST.Core.Health;
using RST.Core.Telemetry;
using RST.Core.Telemetry.Rollups;
using RST.Core.Tray;
using RST.Tests.Telemetry.Rollups;
using Xunit;

namespace RST.Tests.Tray;

public sealed class ModelListTests
{
    /// <summary>A Saturday; its ISO week starts Monday 2026-10-26.</summary>
    private static readonly DateOnly Today = new(2026, 10, 31);

    private static DateOnly Ago(int days) => Today.AddDays(-days);

    private const long Min = 60_000;

    /// <summary>A synthetic model: daily rollup rows turned into an index row by spec #24's builder.</summary>
    private sealed class Model
    {
        public Model(string title, bool central = false, bool cloud = false)
        {
            Doc = central ? SessionBuilder.Central(title) : SessionBuilder.Local(title);
            if (cloud)
            {
                Doc.CloudProjectGuid = Guid.NewGuid().ToString();
                Doc.CloudModelGuid = Guid.NewGuid().ToString();
                Doc.IsCloud = true;
                Doc.IsWorkshared = true;
                Doc.LocalPath = @"C:\Users\me\AppData\Local\Autodesk\Revit\Autodesk Revit 2026\CollaborationCache\" + Guid.NewGuid() + ".rvt";
                Doc.CentralPath = "Autodesk Docs://Project/" + title + ".rvt";
            }
            R = RollupEditor.Create(ModelKey.From(Doc)!.Value, Doc);
            R.Identity = new RollupIdentity
            {
                Title = Doc.Title, LocalPath = Doc.LocalPath, CentralPath = Doc.CentralPath,
                IsWorkshared = Doc.IsWorkshared, IsCloud = Doc.IsCloud,
            };
            R.KeysSeen.Add(Doc);
        }

        public DocumentIdentity Doc { get; }
        public ModelRollup R { get; }
        public string Id => R.ModelId;

        public Model Day(DateOnly date, double activeMin, double openMin = 60, int opens = 1, double openSecSum = 20,
            int syncs = 0, double syncSecSum = 0, string? version = "2026", string? sub = "2026.4", HealthInputs? health = null)
        {
            R.Days.Add(new RollupDay
            {
                Date = date,
                ActiveMs = (long)(activeMin * Min),
                OpenMs = (long)(openMin * Min),
                Opens = opens,
                OpenSumMs = (long)(openSecSum * 1000),
                OpenMaxMs = (long)(openSecSum * 1000),
                Syncs = syncs,
                SyncSumMs = (long)(syncSecSum * 1000),
                SyncMaxMs = (long)(syncSecSum * 1000),
                RevitVersions = version is null ? new() : new() { version },
                RevitSubversions = sub is null ? new() : new() { sub },
                Health = health,
            });
            R.Days.Sort((a, b) => a.Date.CompareTo(b.Date));
            return this;
        }

        public ModelIndexRow Row(DateOnly? writtenOn = null) => RollupEditor.BuildIndexRow(R, writtenOn ?? Today, 0, null);
    }

    private static ModelListIndex Index(params Model[] models) => ModelListIndex.From(models.Select(m => m.Row()));

    private static ModelListResult Q(ModelListIndex index, ModelTimeChip chip = ModelTimeChip.Recent,
        ModelGroupBy groupBy = ModelGroupBy.MajorVersion, string? search = null, string[]? versions = null) =>
        index.Query(new ModelListQuery { Chip = chip, GroupBy = groupBy, Search = search, Versions = versions ?? Array.Empty<string>() }, Today);

    private static string[] Titles(ModelListResult r) => r.Cards.Select(c => c.Title).ToArray();

    // ---- Which models are visible: time chips ----------------------------------------------

    [Fact]
    public void Recent_is_today_and_the_13_days_before_it()
    {
        ModelList.Window(ModelTimeChip.Recent, Today).Should().Be((new DateOnly(2026, 10, 18), Today));
        var inside = new Model("Inside").Day(Ago(13), 5);
        var outside = new Model("Outside").Day(Ago(14), 5);
        var today = new Model("Today").Day(Today, 5);

        Titles(Q(Index(inside, outside, today))).Should().BeEquivalentTo("Inside", "Today");
    }

    [Theory]
    [InlineData(ModelTimeChip.ThreeMonths, 13, "2026-08-03")]
    [InlineData(ModelTimeChip.SixMonths, 26, "2026-05-04")]
    [InlineData(ModelTimeChip.OneYear, 52, "2025-11-03")]
    public void Weekly_chips_cover_their_ISO_weeks_including_the_current_one(ModelTimeChip chip, int weeks, string firstMonday)
    {
        var first = DateOnly.Parse(firstMonday);
        ModelList.WeeksFor(chip).Should().Be(weeks);
        ModelList.Window(chip, Today).Should().Be((first, Today));
        first.DayOfWeek.Should().Be(DayOfWeek.Monday);

        var oldestWeekSunday = new Model("Oldest week").Day(first.AddDays(6), 5);
        var oldestWeekMonday = new Model("Oldest Monday").Day(first, 5);
        var weekBefore = new Model("Week before").Day(first.AddDays(-1), 5); // the Sunday before
        var thisWeek = new Model("This week").Day(new DateOnly(2026, 10, 26), 5);

        Titles(Q(Index(oldestWeekSunday, oldestWeekMonday, weekBefore, thisWeek), chip))
            .Should().BeEquivalentTo("Oldest week", "Oldest Monday", "This week");
    }

    [Fact]
    public void A_model_needs_open_time_in_the_window_and_a_stats_only_day_does_not_count()
    {
        var open = new Model("Open, untouched").Day(Ago(1), activeMin: 0, openMin: 30);
        var statsOnly = new Model("Stats only").Day(Ago(1), activeMin: 0, openMin: 0, opens: 0, openSecSum: 0, version: null, sub: null);

        Titles(Q(Index(open, statsOnly))).Should().Equal("Open, untouched");
    }

    [Fact]
    public void Window_totals_come_from_the_chip_s_days_or_weeks_only()
    {
        var m = new Model("Hospital")
            .Day(Ago(2), 10, opens: 2, openSecSum: 60, syncs: 3, syncSecSum: 90)
            .Day(Ago(20), 100, opens: 1, openSecSum: 30)
            .Day(Ago(200), 1000, opens: 4, openSecSum: 400);
        var index = Index(m);

        Q(index).Cards.Single().ActiveSeconds.Should().Be(10 * 60);
        Q(index, ModelTimeChip.ThreeMonths).Cards.Single().ActiveSeconds.Should().Be(110 * 60);
        Q(index, ModelTimeChip.OneYear).Cards.Single().ActiveSeconds.Should().Be(1110 * 60);
        Q(index, ModelTimeChip.OneYear).Cards.Single().Totals.Should().Be(
            new ModelListTotals(1110 * 60, 180 * 60, 7, 490, 3, 90));
    }

    [Fact]
    public void A_row_written_days_ago_is_filtered_against_today_not_its_write_day()
    {
        var m = new Model("Stale").Day(new DateOnly(2026, 10, 10), 5);
        var row = m.Row(writtenOn: new DateOnly(2026, 10, 12)); // the tray has not folded since

        var r = ModelListIndex.From(new[] { row }).Query(ModelListQuery.Default, Today);

        r.Cards.Should().BeEmpty("2026-10-10 is outside today's 14 days");
        r.EmptyState.Should().Be(ModelListEmptyState.NothingInTimeFrame);
    }

    // ---- Which models are visible: Revit version filter -------------------------------------

    [Fact]
    public void The_version_filter_keeps_models_opened_in_a_selected_major_within_the_window()
    {
        var only26 = new Model("Only 2026").Day(Ago(1), 5, version: "2026", sub: "2026.4");
        var only27 = new Model("Only 2027").Day(Ago(1), 5, version: "2027", sub: "2027.1");
        var both = new Model("Both").Day(Ago(1), 5, version: "2027", sub: "2027.1").Day(Ago(3), 5, version: "2026", sub: "2026.4");
        var old26 = new Model("2026 long ago, 2027 now").Day(Ago(1), 5, version: "2027", sub: "2027.1")
            .Day(Ago(30), 5, version: "2026", sub: "2026.4");
        var index = Index(only26, only27, both, old26);

        Titles(Q(index, versions: new[] { "2026" })).Should().BeEquivalentTo("Only 2026", "Both");
        Titles(Q(index, versions: new[] { "2027" })).Should().BeEquivalentTo("Only 2027", "Both", "2026 long ago, 2027 now");
        Titles(Q(index, versions: new[] { "2026", "2027" })).Should().HaveCount(4);
        Titles(Q(index, ModelTimeChip.ThreeMonths, versions: new[] { "2026" }))
            .Should().BeEquivalentTo("Only 2026", "Both", "2026 long ago, 2027 now");
        Titles(Q(index, versions: new[] { "2025" })).Should().BeEmpty();
        index.Versions.Should().Equal("2027", "2026");
    }

    [Fact]
    public void An_empty_version_selection_is_All()
    {
        var a = new Model("A").Day(Ago(1), 5, version: "2026");
        var b = new Model("B").Day(Ago(1), 5, version: "2025", sub: null);
        Titles(Q(Index(a, b))).Should().HaveCount(2);
        Titles(Q(Index(a, b), versions: new[] { " " })).Should().HaveCount(2, "blank entries are ignored");
    }

    // ---- Grouping -----------------------------------------------------------------------------

    [Fact]
    public void Group_by_major_puts_a_model_in_the_newest_version_it_was_opened_in_within_the_window()
    {
        var upgraded = new Model("Upgraded").Day(Ago(5), 10, version: "2026").Day(Ago(2), 20, version: "2027", sub: "2027.1");
        var upgradedLongAgo = new Model("Upgraded long ago").Day(Ago(1), 30, version: "2026")
            .Day(Ago(60), 20, version: "2027", sub: "2027.1"); // 2027 is outside Recent
        var plain = new Model("Plain 2026").Day(Ago(3), 15, version: "2026");
        var r = Q(Index(upgraded, upgradedLongAgo, plain));

        r.Groups.Select(g => (g.Key, g.Major, g.Count, g.ActiveSeconds)).Should().Equal(
            ("2027", "2027", 1, 30 * 60.0), // Upgraded: its window active time is 10 + 20 min
            ("2026", "2026", 2, 45 * 60.0));
        r.Groups[0].Cards.Single().Title.Should().Be("Upgraded");
        r.Groups[1].Cards.Select(c => c.Title).Should().Equal("Upgraded long ago", "Plain 2026"); // newest last active first

        // At 3M the 2027 day is inside the window, so that model moves up.
        Q(Index(upgraded, upgradedLongAgo, plain), ModelTimeChip.ThreeMonths)
            .Groups[0].Cards.Select(c => c.Title).Should().Equal("Upgraded long ago", "Upgraded");
    }

    [Fact]
    public void With_a_version_filter_a_model_groups_under_the_newest_selected_version()
    {
        // 2026.4 on 2026-10-29, 2027.1 on 2026-10-30 (the PR #179 review's case).
        var both = new Model("Both").Day(Ago(1), 5, version: "2027", sub: "2027.1").Day(Ago(2), 5, version: "2026", sub: "2026.4");
        var index = Index(both);

        var major = Q(index, versions: new[] { "2026" });
        major.Groups.Single().Key.Should().Be("2026", "no group outside the filter appears");
        var minor = Q(index, groupBy: ModelGroupBy.MinorVersion, versions: new[] { "2026" });
        minor.Groups.Single().Key.Should().Be("2026.4");

        // Unfiltered, or with the newer version selected too, it groups under 2027.
        Q(index).Groups.Single().Key.Should().Be("2027");
        Q(index, versions: new[] { "2026", "2027" }).Groups.Single().Key.Should().Be("2027");
    }

    [Fact]
    public void With_a_version_filter_the_card_still_shows_the_true_latest_version_in_the_window()
    {
        var both = new Model("Both").Day(Ago(1), 5, version: "2027", sub: "2027.1").Day(Ago(2), 5, version: "2026", sub: "2026.4");
        var index = Index(both);

        foreach (var by in new[] { ModelGroupBy.MajorVersion, ModelGroupBy.MinorVersion, ModelGroupBy.None })
        {
            var card = Q(index, groupBy: by, versions: new[] { "2026" }).Cards.Single();
            card.Should().BeEquivalentTo(new
            {
                LatestMajor = "2027",
                LatestMinor = "2027.1",
                LatestVersion = "2027.1",
                GroupMajor = "2026",
                GroupMinor = "2026.4",
            });
        }
        Q(index).Cards.Single().Should().BeEquivalentTo(new { LatestVersion = "2027.1", GroupMajor = "2027", GroupMinor = "2027.1" });

        // The latest version is the window's: at Recent a 2027 day 30 days ago does not count.
        var old27 = new Model("Old 2027").Day(Ago(1), 5, version: "2026", sub: "2026.4").Day(Ago(30), 5, version: "2027", sub: "2027.1");
        Q(Index(old27), versions: new[] { "2026" }).Cards.Single().LatestVersion.Should().Be("2026.4");
        Q(Index(old27), ModelTimeChip.ThreeMonths, versions: new[] { "2026" }).Cards.Single().LatestVersion.Should().Be("2027.1");
    }

    [Fact]
    public void Groups_sort_newest_version_first_numerically()
    {
        var models = new[] { "2025", "2027", "2026" }
            .Select(v => new Model("M" + v).Day(Ago(1), 5, version: v, sub: null)).ToArray();
        Q(Index(models)).Groups.Select(g => g.Key).Should().Equal("2027", "2026", "2025");

        var minors = new[] { "2026.4", "2026.10", "2026.3.1", "2026.3" }
            .Select(v => new Model("M" + v).Day(Ago(1), 5, version: "2026", sub: v)).ToArray();
        Q(Index(minors), groupBy: ModelGroupBy.MinorVersion).Groups.Select(g => g.Key)
            .Should().Equal("2026.10", "2026.4", "2026.3.1", "2026.3");
    }

    [Fact]
    public void Group_by_minor_uses_the_newest_minor_of_the_newest_major_and_unknown_minor_without_one()
    {
        var m264 = new Model("On 2026.4").Day(Ago(1), 5, version: "2026", sub: "2026.4").Day(Ago(4), 5, version: "2026", sub: "2026.3");
        var m263 = new Model("On 2026.3").Day(Ago(2), 5, version: "2026", sub: "2026.3");
        var old264 = new Model("2026.4 long ago").Day(Ago(1), 5, version: "2026", sub: null)
            .Day(Ago(40), 5, version: "2026", sub: "2026.4"); // its minor is outside Recent
        var preV32 = new Model("Before v3.2").Day(Ago(3), 5, version: "2026", sub: null);
        var m271 = new Model("On 2027.1").Day(Ago(3), 5, version: "2027", sub: "2027.1").Day(Ago(1), 5, version: "2026", sub: "2026.4");
        var r = Q(Index(m264, m263, old264, preV32, m271), groupBy: ModelGroupBy.MinorVersion);

        r.Groups.Select(g => (g.Key, g.Major, g.Minor, g.IsUnknownMinor)).Should().Equal(
            ("2027.1", "2027", "2027.1", false),
            ("2026.4", "2026", "2026.4", false),
            ("2026.3", "2026", "2026.3", false),
            ("2026/unknown", "2026", (string?)null, true));
        r.Groups[3].Cards.Select(c => c.Title).Should().Equal("2026.4 long ago", "Before v3.2");
        r.Groups[3].Version.Should().Be("2026");
        r.Cards.Single(c => c.Title == "Before v3.2").LatestVersion.Should().Be("2026");
        r.Cards.Single(c => c.Title == "On 2026.4").LatestVersion.Should().Be("2026.4");
    }

    [Fact]
    public void Group_by_none_is_one_flat_list_newest_last_active_first()
    {
        var a = new Model("A").Day(Ago(5), 5, version: "2027");
        var b = new Model("B").Day(Ago(1), 5, version: "2025");
        var c = new Model("C").Day(Ago(3), 5, version: "2026");
        var r = Q(Index(a, b, c), groupBy: ModelGroupBy.None);

        r.Groups.Should().ContainSingle();
        r.Groups[0].Should().BeEquivalentTo(new { Key = "all", GroupBy = ModelGroupBy.None, Major = (string?)null, IsUnknownVersion = false, Count = 3 });
        Titles(r).Should().Equal("B", "C", "A");
    }

    [Fact]
    public void Ties_on_last_active_sort_by_title_then_id_so_the_order_is_stable()
    {
        var models = new[] { "beta", "Alpha", "gamma" }.Select(t => new Model(t).Day(Ago(1), 5)).ToArray();
        Titles(Q(Index(models))).Should().Equal("Alpha", "beta", "gamma");
    }

    [Fact]
    public void A_model_with_no_version_recorded_groups_under_unknown_last_and_drops_out_under_a_filter()
    {
        var known = new Model("Known").Day(Ago(2), 5, version: "2026");
        var none = new Model("No version").Day(Ago(1), 5, version: null, sub: null);
        var r = Q(Index(known, none));

        r.Groups.Select(g => (g.Key, g.IsUnknownVersion)).Should().Equal(("2026", false), ("unknown", true));
        r.Cards.Single(c => c.Title == "No version").LatestVersion.Should().BeNull();
        Titles(Q(Index(known, none), versions: new[] { "2026" })).Should().Equal("Known");
    }

    // ---- Aggregate cards ---------------------------------------------------------------------

    [Fact]
    public void Aggregate_cards_sum_the_visible_cards_and_average_by_count()
    {
        var a = new Model("A", central: true).Day(Ago(1), 30, opens: 2, openSecSum: 50, syncs: 4, syncSecSum: 100);
        var b = new Model("B", central: true).Day(Ago(2), 15, opens: 3, openSecSum: 25, syncs: 1, syncSecSum: 60);
        var hidden = new Model("Hidden").Day(Ago(40), 600, opens: 9, openSecSum: 900);
        var r = Q(Index(a, b, hidden));

        r.Aggregates.Should().Be(new ModelListAggregates(
            Models: 2,
            ActiveSeconds: 45 * 60,
            Opens: 5,
            AverageOpenSeconds: 75.0 / 5,
            Syncs: 5,
            AverageSyncSeconds: 160.0 / 5));
    }

    [Fact]
    public void Avg_sync_time_is_null_with_no_syncs_and_avg_open_is_null_with_no_opens()
    {
        var local = new Model("Local").Day(Ago(1), 30, opens: 0, openSecSum: 0, syncs: 0);
        var r = Q(Index(local));

        r.Aggregates.Should().Be(new ModelListAggregates(1, 30 * 60, 0, null, 0, null));
        r.Cards.Single().Totals.AverageSyncSeconds.Should().BeNull();
        Q(Index()).Aggregates.Should().Be(ModelListAggregates.Zero);
    }

    // ---- Model card ----------------------------------------------------------------------------

    [Fact]
    public void A_card_carries_title_file_name_health_version_last_active_and_window_active_time()
    {
        var health = new HealthInputs(4L * 1024 * 1024 * 1024, 0, 300, 300); // size 25, density 0, open 25, sync 25 → 25 → Amber
        var m = new Model("Hospital", central: true)
            .Day(Ago(9), 12, version: "2026", sub: "2026.3")
            .Day(Ago(4), 18, version: "2026", sub: "2026.4", health: health);
        var card = Q(Index(m)).Cards.Single();

        card.Should().BeEquivalentTo(new
        {
            ModelId = m.Id,
            Title = "Hospital",
            FileName = "Hospital_me.rvt",
            ShowFileName = true,
            LocalPath = @"C:\Users\me\Documents\Hospital_me.rvt",
            CentralPath = @"\\server\projects\Hospital.rvt",
            IsWorkshared = true,
            IsCloud = false,
            Marker = ModelMarker.Workshared,
            Health = ModelHealthColour.Amber,
            HealthSeverity = HealthSeverity.Warn,
            LatestMajor = "2026",
            LatestMinor = "2026.4",
            LatestVersion = "2026.4",
            LastActiveDay = Ago(4),
            FirstActiveDay = Ago(9),
            ActiveSeconds = 30 * 60.0,
        });
    }

    [Fact]
    public void Health_comes_from_the_current_inputs_through_the_score_function()
    {
        var green = new Model("Green").Day(Ago(1), 5, health: new HealthInputs(100L * 1024 * 1024, 0, 10, 10));
        var red = new Model("Red").Day(Ago(1), 5, health: new HealthInputs(9_000L * 1024 * 1024, 90_000, 900, 900));
        var grey = new Model("Grey").Day(Ago(1), 5, health: null);
        var latestWins = new Model("Latest wins")
            .Day(Ago(3), 5, health: new HealthInputs(9_000L * 1024 * 1024, 90_000, 900, 900))
            .Day(Ago(1), 5, health: new HealthInputs(null, null, 10, null));
        var cards = Q(Index(green, red, grey, latestWins)).Cards.ToDictionary(c => c.Title);

        cards["Green"].Health.Should().Be(ModelHealthColour.Green);
        cards["Red"].Health.Should().Be(ModelHealthColour.Red);
        cards["Red"].HealthSeverity.Should().Be(HealthSeverity.Danger);
        cards["Grey"].Health.Should().Be(ModelHealthColour.Grey);
        cards["Grey"].HealthSeverity.Should().Be(HealthSeverity.None);
        cards["Latest wins"].Health.Should().Be(ModelHealthColour.Green);
    }

    [Fact]
    public void Markers_and_file_names_follow_the_model_kind()
    {
        var local = new Model("Local").Day(Ago(1), 5);
        var cloud = new Model("Cloud", cloud: true).Day(Ago(1), 5);
        var cards = Q(Index(local, cloud)).Cards.ToDictionary(c => c.Title);

        cards["Local"].Marker.Should().Be(ModelMarker.None);
        cards["Local"].FileName.Should().Be("Local.rvt");
        cards["Local"].ShowFileName.Should().BeFalse("the title is the file name without its extension");
        cards["Cloud"].Marker.Should().Be(ModelMarker.Cloud);
        cards["Cloud"].IsWorkshared.Should().BeTrue();
        cards["Cloud"].ShowFileName.Should().BeFalse("a cloud model's local file is a cache file");
    }

    [Fact]
    public void A_model_without_a_title_is_titled_by_its_file_name()
    {
        var m = new Model("Untitled").Day(Ago(1), 5);
        var row = m.Row();
        row.Title = null;
        row.LocalPath = @"C:\models\Clinic.rvt";
        var card = ModelListIndex.From(new[] { row }).Query(ModelListQuery.Default, Today).Cards.Single();

        card.Title.Should().Be("Clinic");
        card.ShowFileName.Should().BeFalse();
        ModelList.FileNameOf(@"\\srv\share\Tower.rvt").Should().Be("Tower.rvt");
        ModelList.FileNameOf("C:/a/b/C.rvt").Should().Be("C.rvt");
        ModelList.FileNameOf("  ").Should().BeNull();
    }

    // ---- Empty states and the detail's starting range -------------------------------------------

    [Fact]
    public void Empty_states_tell_no_models_from_filters_and_from_search()
    {
        var none = Q(Index());
        none.EmptyState.Should().Be(ModelListEmptyState.NoModels);
        Q(Index(), search: "x").EmptyState.Should().Be(ModelListEmptyState.NoModels, "an empty index says so even while searching");
        none.Groups.Should().BeEmpty();

        var index = Index(new Model("Old").Day(Ago(100), 5, version: "2026"));
        var recent = Q(index);
        recent.EmptyState.Should().Be(ModelListEmptyState.NothingInTimeFrame);
        recent.OfferOneYear.Should().BeTrue();
        recent.Groups.Should().BeEmpty();
        recent.Aggregates.Should().Be(ModelListAggregates.Zero);

        var oneYear = Q(index, ModelTimeChip.OneYear, versions: new[] { "2027" });
        oneYear.EmptyState.Should().Be(ModelListEmptyState.NothingInTimeFrame);
        oneYear.OfferOneYear.Should().BeFalse("it is on 1Y already");
        Q(index, ModelTimeChip.OneYear).EmptyState.Should().Be(ModelListEmptyState.None);

        Q(index, search: "nothing like it").EmptyState.Should().Be(ModelListEmptyState.NoMatches);
        Q(index, search: "nothing like it").OfferOneYear.Should().BeFalse();
    }

    [Theory]
    [InlineData(ModelTimeChip.Recent, null, ModelDetailRange.OneMonth)]
    [InlineData(ModelTimeChip.ThreeMonths, null, ModelDetailRange.ThreeMonths)]
    [InlineData(ModelTimeChip.SixMonths, null, ModelDetailRange.SixMonths)]
    [InlineData(ModelTimeChip.OneYear, null, ModelDetailRange.OneYear)]
    [InlineData(ModelTimeChip.Recent, "hosp", ModelDetailRange.OneYear)]
    [InlineData(ModelTimeChip.SixMonths, "hosp", ModelDetailRange.OneYear)]
    public void The_detail_opens_on_the_range_nearest_the_chip_and_on_1Y_while_searching(ModelTimeChip chip, string? search, ModelDetailRange expected)
    {
        Q(Index(), chip, search: search).DetailRange.Should().Be(expected);
        ((int)ModelDetailRange.OneMonth).Should().Be(30, "the shortest pill covering two weeks, in ActivityRanges days");
    }

    // ---- Search (AC 2) ---------------------------------------------------------------------------

    private static (ModelListIndex Index, Model Old, Model Recent) SearchFixture()
    {
        var old = new Model("Hospital East", central: true)
            .Day(Today.AddYears(-2), 50, opens: 2, openSecSum: 40, syncs: 2, syncSecSum: 30, version: "2024", sub: "2024.2")
            .Day(Today.AddYears(-2).AddDays(1), 70, opens: 1, openSecSum: 20, syncs: 1, syncSecSum: 15, version: "2024", sub: "2024.2");
        var recent = new Model("Clinic").Day(Ago(1), 10, version: "2026");
        return (Index(old, recent), old, recent);
    }

    [Theory]
    [InlineData("hospital east")]           // title, case-insensitive
    [InlineData("HOSPITAL EAST_ME.RVT")]     // local file name
    [InlineData(@"documents\hosp")]          // local path
    [InlineData(@"\\SERVER\projects")]       // central path
    [InlineData("  east  ")]                 // trimmed
    public void Search_finds_a_model_inactive_for_two_years_by_title_file_name_and_path(string search)
    {
        var (index, old, _) = SearchFixture();
        Q(index).Cards.Should().NotContain(c => c.ModelId == old.Id, "it is far outside Recent");

        var r = Q(index, search: search);

        r.Cards.Select(c => c.ModelId).Should().Equal(old.Id);
        r.IsSearching.Should().BeTrue();
        r.WindowFirst.Should().BeNull();
        r.WindowLast.Should().BeNull();
    }

    [Fact]
    public void While_searching_the_chip_and_version_filter_are_ignored_and_every_number_is_all_time()
    {
        var (index, old, _) = SearchFixture();

        var r = Q(index, ModelTimeChip.Recent, ModelGroupBy.MinorVersion, search: "hospital", versions: new[] { "2026" });

        var card = r.Cards.Single();
        card.ModelId.Should().Be(old.Id);
        card.ActiveSeconds.Should().Be(120 * 60);
        card.LatestVersion.Should().Be("2024.2", "the newest version all time");
        r.Groups.Single().Key.Should().Be("2024.2");
        r.Aggregates.Should().Be(new ModelListAggregates(1, 120 * 60, 3, 60.0 / 3, 3, 45.0 / 3));
        r.Query.Versions.Should().Equal("2026");
    }

    [Fact]
    public void Clearing_the_search_restores_the_filters()
    {
        var (index, _, recent) = SearchFixture();
        var filtered = new ModelListQuery { Chip = ModelTimeChip.Recent, Versions = new[] { "2026" } };

        Q(index, search: "hospital").Cards.Should().ContainSingle();
        foreach (var cleared in new[] { null, "", "   " })
        {
            var r = index.Query(filtered with { Search = cleared }, Today);
            r.IsSearching.Should().BeFalse();
            r.WindowFirst.Should().Be(Ago(13));
            r.Cards.Select(c => c.ModelId).Should().Equal(recent.Id);
        }
    }

    [Fact]
    public void Search_also_finds_a_model_by_a_title_or_path_it_had_before()
    {
        var m = new Model("Renamed").Day(Ago(1), 5);
        m.R.KeysSeen.Add(new DocumentIdentity { Title = "Old Name", LocalPath = @"D:\moved\from\here.rvt" });
        m.R.KeysSeen.Add(m.Doc); // the current identity is the most recent
        var index = Index(m);

        Q(index, search: "old name").Cards.Should().ContainSingle();
        Q(index, search: @"d:\moved").Cards.Should().ContainSingle();
    }

    // ---- Implausible values and odd rows ----------------------------------------------------------

    [Fact]
    public void Implausible_index_values_are_clamped_for_display()
    {
        var m = new Model("Hand edited").Day(Ago(1), 5).Day(Ago(2), 5);
        var row = m.Row();
        row.RecentDaysTotals[0].ActiveMs = -5_000;            // negative
        row.RecentDaysTotals[0].Opens = -3;
        row.RecentDaysTotals[1].ActiveMs = 30L * 3_600_000;    // over 24 h in a day
        row.RecentDaysTotals[1].SyncSumMs = long.MaxValue;
        row.RecentDaysTotals[1].Syncs = 1;
        row.RecentWeeksTotals[0].ActiveMs = 200L * 3_600_000;  // over 7 × 24 h in a week
        row.Totals.ActiveMs = 1_000L * 3_600_000;             // over 24 h × the 2 days between first and last active
        var index = ModelListIndex.From(new[] { row });

        var recent = Q(index).Cards.Single();
        recent.Totals.ActiveSeconds.Should().Be(24 * 3600);
        recent.Totals.Opens.Should().Be(1);
        recent.Totals.SyncSecondsSum.Should().Be(24 * 3600);
        Q(index, ModelTimeChip.ThreeMonths).Cards.Single().ActiveSeconds.Should().Be(7 * 24 * 3600);
        Q(index, search: "hand").Cards.Single().ActiveSeconds.Should().Be(2 * 24 * 3600);
    }

    [Fact]
    public void Null_rows_rows_without_an_id_and_repeated_ids_are_skipped()
    {
        var a = new Model("A").Day(Ago(1), 5).Row();
        var again = new Model("A again").Day(Ago(1), 5).Row();
        again.ModelId = a.ModelId;
        var noId = new Model("No id").Day(Ago(1), 5).Row();
        noId.ModelId = " ";

        var index = ModelListIndex.From(new ModelIndexRow?[] { null, a, again, noId });

        index.Count.Should().Be(1);
        Titles(Q(index)).Should().Equal("A");
    }

    [Fact]
    public void The_version_filter_choices_are_every_major_in_the_index_newest_first()
    {
        var a = new Model("A").Day(Ago(400), 5, version: "2025", sub: null);
        var b = new Model("B").Day(Ago(1), 5, version: "2027", sub: "2027.1");
        var c = new Model("C").Day(Ago(1), 5, version: null, sub: "2026.4"); // a subversion implies its major
        var r = Q(Index(a, b, c));

        r.AvailableVersions.Should().Equal("2027", "2026", "2025");
        r.IndexedModels.Should().Be(3);
        r.Cards.Single(x => x.Title == "C").LatestVersion.Should().Be("2026.4");
    }

    [Fact]
    public void Today_comes_from_the_injected_clock_and_zone()
    {
        // 2026-11-01 03:00 UTC is still 2026-10-31 at UTC-4.
        var ny = TimeZoneInfo.CreateCustomTimeZone("UTC-4", TimeSpan.FromHours(-4), "UTC-4", "UTC-4");
        var now = new DateTimeOffset(2026, 11, 1, 3, 0, 0, TimeSpan.Zero);
        ModelList.LocalToday(now, ny).Should().Be(Today);
        ModelList.LocalToday(now, TimeZoneInfo.Utc).Should().Be(new DateOnly(2026, 11, 1));

        var m = new Model("Edge").Day(Ago(13), 5);
        Index(m).Query(ModelListQuery.Default, now, ny).Cards.Should().ContainSingle();
        Index(m).Query(ModelListQuery.Default, now, TimeZoneInfo.Utc).Cards.Should().BeEmpty();
    }

    [Fact]
    public void Revit_versions_compare_in_release_order()
    {
        var order = RevitVersionOrder.Instance;
        order.Compare("2026.10", "2026.4").Should().BePositive();
        order.Compare("2027", "2026.9").Should().BePositive();
        order.Compare("2026.3.1", "2026.3").Should().BePositive();
        order.Compare("2026", "2026").Should().Be(0);
        order.Compare(null, "2026").Should().BeNegative();
        ModelList.MajorOf("2026.4").Should().Be("2026");
        ModelList.MajorOf("2026").Should().Be("2026");
    }
}
