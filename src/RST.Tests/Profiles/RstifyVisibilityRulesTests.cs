using FluentAssertions;
using RST.Core.Profiles;
using Xunit;

namespace RST.Tests.Profiles;

public sealed class RstifyVisibilityRulesTests
{
    [Theory]
    [InlineData("Steel", true, false, false, true)]
    [InlineData("Systems", true, false, false, true)]
    [InlineData("Steel", false, false, false, false)]
    [InlineData("Steel", true, true, false, false)]
    [InlineData("Add-Ins", true, false, true, false)]
    [InlineData("My Profile", true, false, true, false)]
    [InlineData("View", true, false, false, false)]
    [InlineData("steel", true, false, false, false)]
    [InlineData(null, true, false, false, false)]
    public void Rehide_only_visible_tracked_tabs_outside_self_mutation(string? title, bool visible,
        bool selfMutation, bool protectedTab, bool expected) =>
        RstifyVisibilityRules.ShouldRehide(["Steel", "Systems", "Add-Ins", "My Profile"], title,
            visible, selfMutation, protectedTab).Should().Be(expected);

    [Fact]
    public void A_lifted_rule_does_not_rehide_tabs() =>
        RstifyVisibilityRules.ShouldRehide([], "Steel", true, false, false).Should().BeFalse();

    [Fact]
    public void A_replacement_rule_only_protects_its_new_titles()
    {
        RstifyVisibilityRules.ShouldRehide(["View"], "Steel", true, false, false).Should().BeFalse();
        RstifyVisibilityRules.ShouldRehide(["View"], "View", true, false, false).Should().BeTrue();
    }

    [Fact]
    public void A_setter_that_always_reshows_yields_until_the_background_refill()
    {
        var budget = new RstifyReassertBudget();
        var normal = new Queue<Action>();
        var background = new Queue<Action>();
        var visible = true;
        var hides = 0;

        void OnVisible()
        {
            if (!RstifyVisibilityRules.ShouldRehide(["Steel"], "Steel", visible, false, false)) return;
            if (background.Count == 0) background.Enqueue(budget.Refill);
            if (!budget.TryTake()) return;
            normal.Enqueue(() =>
            {
                visible = false;
                hides++;
                // Simulate a binding asynchronously undoing every IsVisible=false setter.
                normal.Enqueue(() => { visible = true; OnVisible(); });
            });
        }

        void DrainNormal()
        {
            // Bound the fake scheduler too: a regression fails instead of hanging the suite.
            for (var i = 0; i < 20 && normal.TryDequeue(out var callback); i++) callback();
            normal.Should().BeEmpty("Input and Render must get a turn despite the binding");
        }

        OnVisible();
        DrainNormal();
        hides.Should().Be(3);
        visible.Should().BeTrue("remaining drift is left to the Idling backstop");
        OnVisible();
        normal.Should().BeEmpty("further notifications cannot bypass the spent budget");
        background.Should().ContainSingle();

        background.Dequeue()(); // Input and Render have now had their chance.
        normal.Should().BeEmpty("refill alone must not start another correction loop");
        OnVisible();
        DrainNormal();
        hides.Should().Be(6);
    }
}
