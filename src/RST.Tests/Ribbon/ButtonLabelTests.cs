using FluentAssertions;
using RST.Core.Ribbon;
using Xunit;

namespace RST.Tests.Ribbon;

public class ButtonLabelTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("",   "")]
    [InlineData("Save",          "Save")]              // length 4 — under threshold
    [InlineData("Drafts",        "Drafts")]            // length 6 — exactly threshold, no wrap
    [InlineData("Drafter",       "Drafter")]           // length 7 — no space at all
    [InlineData("Long Tool",     "Long Tool")]         // space at idx 4, before threshold
    [InlineData("Longer Tool",   "Longer\nTool")]      // space at idx 6 — qualifies
    [InlineData("Open Existing Project", "Open Existing\nProject")]  // first space ≥6 is at idx 13
    [InlineData("Make Big Drawing Now",  "Make Big\nDrawing Now")]   // first space ≥6 is at idx 8
    [InlineData("Reload All Profiles",   "Reload\nAll Profiles")]    // space at idx 6
    [InlineData("Stack Walls Multiword", "Stack Walls\nMultiword")]  // space at idx 5 disqualified (<6); next space at idx 11 wins
    public void Wrap_returns_expected(string? input, string expected)
    {
        ButtonLabel.Wrap(input).Should().Be(expected);
    }
}
