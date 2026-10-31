// SC-051 (decision #63): a profile saved before v3.1.1 may hold a button Revit doesn't let add-ins
// run (Text Types). Its click shows SlotMessages.NotPostable (SlotInvokeCommand catches PostCommand's
// ArgumentException), a plain message, not the "failed unexpectedly" one rc.3 showed (F1).

using FluentAssertions;
using RST.Core.Ribbon;
using Xunit;

namespace RST.Tests.Ribbon;

public sealed class SlotMessagesTests
{
    [Fact]
    public void A_button_Revit_wont_let_add_ins_run_gets_a_plain_message()
    {
        var message = SlotMessages.NotPostable("Text Types");

        message.MainInstruction.Should().Be("“Text Types” can’t run from an RST button.");
        message.MainContent.Should().StartWith("Revit doesn’t let add-ins run this command");
        message.MainContent.Should().Contain("Revit’s own ribbon").And.Contain("Builder");
        message.MainContent.Should().NotContain("failed unexpectedly").And.NotContain("%AppData%");
    }

    [Fact]
    public void The_other_two_messages_are_unchanged()
    {
        SlotMessages.MissingAddin("Gantt-It").Should().Be(new SlotMessage(
            "“Gantt-It” couldn’t run.",
            "The add-in this button needs doesn’t appear to be installed in this Revit version " +
            "(or it is disabled in Revit’s Add-In Manager). Install or enable the add-in and try again."));
        SlotMessages.Failure("Wall", "boom").Should().Be(new SlotMessage(
            "“Wall” couldn’t run.",
            "The button failed unexpectedly: boom\n\nDetails are in %AppData%\\RST\\logs\\."));
    }
}
