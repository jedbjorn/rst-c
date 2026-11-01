using Autodesk.Windows;
using FluentAssertions;
using RST.Engine.Scanning;
using RST.RuntimeTests.Support;
using Xunit;

namespace RST.RuntimeTests.Ribbon;

public sealed class RibbonTabEnumeratorTests
{
    [Fact]
    public void Family_and_mode_ids_are_excluded_but_ordinary_and_null_ids_are_kept() => Screenshots.RunSta(() =>
    {
        RibbonTabEnumerator.EnumerateTabs(
        [
            new RibbonTab { Title = "Create", Id = "Home_Family" },
            new RibbonTab { Title = "Modify", Id = "Second_Modify" },
            new RibbonTab { Title = "Architecture", Id = "Home", IsVisible = false },
            new RibbonTab { Title = "Modify", Id = "Modify" },
            new RibbonTab { Title = "Unknown", Id = null },
        ]).Should().Equal("Architecture", "Modify", "Unknown");
    });

    [Fact]
    public void Blank_contextual_restricted_and_duplicate_titles_are_still_excluded() => Screenshots.RunSta(() =>
    {
        RibbonTabEnumerator.EnumerateTabs(
        [
            null,
            new RibbonTab { Title = "", Id = "Blank" },
            new RibbonTab { Title = "Modify | Walls", Id = "Walls", IsContextualTab = true },
            new RibbonTab { Title = "Family Editor", Id = "Other" },
            new RibbonTab { Title = "Structure", Id = "Structure" },
            new RibbonTab { Title = "Structure", Id = "Duplicate" },
        ]).Should().Equal("Structure");
    });
}
