// ModelHealthTests.cs — spec #24 AC 4a: the health score and colour follow
// decision #55. Each threshold, midpoints, band edges, every missing-input
// case and all-missing (grey). The per-day inputs recomputing forward after a
// late fold are in RollupFoldTests.

using FluentAssertions;
using RST.Core.Health;
using Xunit;

namespace RST.Tests.Health;

public sealed class ModelHealthTests
{
    private const long Mb = 1024L * 1024L;

    /// <summary>All four inputs, healthy unless overridden.</summary>
    private static HealthInputs All(double sizeMb = 100, int? warnings = 0, double? open = 10, double? sync = 10) =>
        new((long)(sizeMb * Mb), warnings, open, sync);

    private static double Score(HealthInputs i) => ModelHealth.Evaluate(i).Score!.Value;

    [Fact]
    public void At_or_below_every_no_penalty_point_the_score_is_100_and_green()
    {
        var r = ModelHealth.Evaluate(All(sizeMb: 500, warnings: 250, open: 30, sync: 30)); // 0.5 warnings / MB
        r.Score.Should().Be(100);
        r.Colour.Should().Be(ModelHealthColour.Green);
    }

    [Fact]
    public void At_or_beyond_every_full_penalty_point_the_score_is_0_and_red()
    {
        var r = ModelHealth.Evaluate(All(sizeMb: 4096, warnings: 4096 * 2, open: 300, sync: 300));
        r.Score.Should().Be(0);
        r.Colour.Should().Be(ModelHealthColour.Red);
        ModelHealth.Evaluate(All(sizeMb: 9000, warnings: 90000, open: 9000, sync: 9000)).Score.Should().Be(0);
    }

    [Theory]
    [InlineData(500, 100)]
    [InlineData(4096, 75)]
    [InlineData(2298, 87.5)]   // halfway between 500 MB and 4 GB
    public void Size_penalty_is_linear_between_500_MB_and_4_GB(double sizeMb, double expected)
    {
        Score(All(sizeMb: sizeMb)).Should().BeApproximately(expected, 1e-9);
    }

    [Theory]
    [InlineData(250, 100)]    // 0.5 per MB on 500 MB (no size penalty yet)
    [InlineData(1000, 75)]    // 2 per MB
    [InlineData(625, 87.5)]   // 1.25 per MB, the midpoint
    public void Warning_density_penalty_is_linear_between_half_and_two_per_MB(int warnings, double expected)
    {
        Score(All(sizeMb: 500, warnings: warnings)).Should().BeApproximately(expected, 1e-9);
    }

    [Theory]
    [InlineData(30, 100)]
    [InlineData(300, 75)]
    [InlineData(165, 87.5)]
    public void Open_and_sync_penalties_are_linear_between_30_seconds_and_5_minutes(double seconds, double expected)
    {
        Score(All(open: seconds)).Should().BeApproximately(expected, 1e-9);
        Score(All(sync: seconds)).Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void Band_edges_75_50_25_land_green_yellow_amber()
    {
        // One, two, three inputs at full penalty: 75, 50, 25.
        ModelHealth.Evaluate(All(open: 300)).Should().Be(new ModelHealthResult(75, ModelHealthColour.Green));
        ModelHealth.Evaluate(All(open: 300, sync: 300)).Should().Be(new ModelHealthResult(50, ModelHealthColour.Yellow));
        ModelHealth.Evaluate(All(sizeMb: 4096, warnings: 0, open: 300, sync: 300))
            .Should().Be(new ModelHealthResult(25, ModelHealthColour.Amber));
        ModelHealth.ColourFor(74.99).Should().Be(ModelHealthColour.Yellow);
        ModelHealth.ColourFor(49.99).Should().Be(ModelHealthColour.Amber);
        ModelHealth.ColourFor(24.99).Should().Be(ModelHealthColour.Red);
        ModelHealth.ColourFor(0).Should().Be(ModelHealthColour.Red);
    }

    [Fact]
    public void No_inputs_at_all_is_grey_not_enough_data()
    {
        ModelHealth.Evaluate(HealthInputs.None).Should().Be(new ModelHealthResult(null, ModelHealthColour.Grey));
        ModelHealth.Evaluate(null).Colour.Should().Be(ModelHealthColour.Grey);
        ModelHealth.ToSeverity(ModelHealthColour.Grey).Should().Be(HealthSeverity.None);
    }

    [Fact]
    public void A_local_model_without_syncs_scales_the_other_three_to_100()
    {
        // Open at full penalty is a third of the score once sync is left out.
        Score(new HealthInputs(100 * Mb, 0, 300, null)).Should().BeApproximately(100 - 100.0 / 3, 1e-9);
    }

    [Fact]
    public void A_cloud_model_without_a_file_size_loses_size_and_density()
    {
        // Warnings are known but density needs the size: only open and sync remain.
        var r = ModelHealth.Evaluate(new HealthInputs(null, 99999, 300, 30));
        r.Score.Should().BeApproximately(50, 1e-9);
        r.Colour.Should().Be(ModelHealthColour.Yellow);
    }

    [Fact]
    public void Missing_warnings_leave_density_out_but_keep_size()
    {
        Score(new HealthInputs(4096 * Mb, null, null, null)).Should().Be(0);
        Score(new HealthInputs(500 * Mb, null, null, null)).Should().Be(100);
    }

    [Fact]
    public void A_single_present_input_carries_the_whole_score()
    {
        Score(new HealthInputs(null, null, 165, null)).Should().BeApproximately(50, 1e-9);
        ModelHealth.Evaluate(new HealthInputs(null, null, null, 300)).Colour.Should().Be(ModelHealthColour.Red);
    }

    [Fact]
    public void Colours_map_to_Healths_four_severity_levels()
    {
        ModelHealth.ToSeverity(ModelHealthColour.Green).Should().Be(HealthSeverity.Ok);
        ModelHealth.ToSeverity(ModelHealthColour.Yellow).Should().Be(HealthSeverity.WarnSoft);
        ModelHealth.ToSeverity(ModelHealthColour.Amber).Should().Be(HealthSeverity.Warn);
        ModelHealth.ToSeverity(ModelHealthColour.Red).Should().Be(HealthSeverity.Danger);
    }

    [Fact]
    public void Median_of_odd_and_even_counts()
    {
        ModelHealth.Median(new double[] { 5, 1, 3 }).Should().Be(3);
        ModelHealth.Median(new double[] { 4, 1, 3, 2 }).Should().Be(2.5);
        ModelHealth.Median(new double[0]).Should().BeNull();
    }
}
