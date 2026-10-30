// rst-c's own (not vendored): the xUnit port of tests/ui/failed-disables.test.mjs (spec #16, WU3,
// AC5), every case, against FailedDisableMessages.Message, the C# port of profile_loader.html's
// failedDisableMessage.
//
// The bug it locks down (flag #15, doc #4 addendum): the bridge reports add-ins the disable pass
// could not rename as { fileName }, but the UI mapped .name, so the list always filtered to empty
// and the failure message degenerated. With a 3-second toast racing the Loader's auto-close,
// 5 failed disables shipped as "all clear".

using FluentAssertions;
using RST.Core.Loader;
using Xunit;

namespace RST.Tests.Loader;

public class FailedDisableMessagesTests
{
    [Fact]
    public void Empty_or_missing_list_gives_null_nothing_to_surface()
    {
        FailedDisableMessages.Message([]).Should().BeNull();
        FailedDisableMessages.Message(null).Should().BeNull();
    }

    [Fact]
    public void Bridge_shape_fileName_is_read_the_flag_15_mapping_bug()
    {
        var msg = FailedDisableMessages.Message([new FailedDisable("BatchPrint.addin")]);
        msg.Should().NotBeNull();
        msg.Should().Contain("BatchPrint.addin");
        msg.Should().Be("BatchPrint.addin was not disabled. Administrator permissions are required to disable add-ins installed for all users.");
    }

    [Fact]
    public void Multiple_failures_list_every_file_name()
    {
        var failed = new[] { "BatchPrint", "eTransmit", "TotalCarbonAnalysis", "WorksharingMonitor", "FormItConverter" }
            .Select(n => new FailedDisable(n + ".addin")).ToList();
        var msg = FailedDisableMessages.Message(failed);
        foreach (var f in failed) msg.Should().Contain(f.FileName);
        msg.Should().StartWith("The following add-ins were not disabled (administrator permissions required): ");
        msg.Should().EndWith("BatchPrint.addin, eTransmit.addin, TotalCarbonAnalysis.addin, WorksharingMonitor.addin, FormItConverter.addin");
    }

    [Fact]
    public void Legacy_shape_name_still_surfaces()
    {
        var msg = FailedDisableMessages.Message([new FailedDisable(null, Name: "Old.addin")]);
        msg.Should().NotBeNull();
        msg.Should().Contain("Old.addin");
    }

    [Fact]
    public void Unknown_entry_shape_still_produces_a_message_never_silent()
    {
        var msg = FailedDisableMessages.Message([new FailedDisable(null)]);
        msg.Should().NotBeNullOrEmpty("a failure with no readable name must still surface");
        msg.Should().Be("Some add-ins could not be disabled. Administrator permissions are required to disable add-ins installed for all users.");
    }

    [Fact]
    public void The_subsystem_failure_entry_names_itself()
    {
        var msg = FailedDisableMessages.Message([new FailedDisable("(disable subsystem failure)", Error: "boom")]);
        msg.Should().StartWith("(disable subsystem failure) was not disabled.");
        FailedDisableMessages.Title.Should().Be("Some Add-ins Were Not Disabled");
    }
}
