// Copied from splash-it tests/SplashIt.Core.Tests/Colors/RecentColorsTests.cs (splash-it commit ba86a71; originally gantt-it tests/GanttIt.Core.Tests/Editing/EditingTests.cs, class RecentColorsTests, gantt-it commit a96d0a1), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); explicit Xunit usings.

using RST.Core.Colors;
using Xunit;

namespace RST.Tests.Colors;

public class RecentColorsTests
{
    private static readonly Rgb Red = Rgb.Parse("#FF0000");
    private static readonly Rgb Green = Rgb.Parse("#00FF00");
    private static readonly Rgb Blue = Rgb.Parse("#0000FF");

    [Fact]
    public void Most_recent_first_without_duplicates()
    {
        var recent = new RecentColors();
        recent.Add(Red);
        recent.Add(Green);
        recent.Add(Red);

        Assert.Equal([Red, Green], recent.Items);
    }

    [Fact]
    public void Oldest_drops_off_at_capacity()
    {
        var recent = new RecentColors(capacity: 2);
        recent.Add(Red);
        recent.Add(Green);
        recent.Add(Blue);

        Assert.Equal([Blue, Green], recent.Items);
    }

    [Fact]
    public void Capacity_must_be_positive() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecentColors(0));
}
