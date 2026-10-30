// rst-c's own (not vendored): HealthService's guards (spec #16, WU2 review N2, N5): one scan or
// clean at a time in the process, and the cleanup id re-check that keeps a clean to the active
// profile's enabled targets.

using RST.Core.Profiles;
using RST.UI.Health;
using Xunit;

namespace RST.RuntimeTests.Health;

public class HealthServiceTests
{
    private static CleanupTarget Target(string id, string name, bool enabled = true) =>
        new() { Id = id, Name = name, Path = @"%LocalAppData%\Autodesk\Revit\" + name, Enabled = enabled };

    [Fact]
    public void SelectTargets_KeepsOnlyEnabledAllowedTargets_ByIdCaseInsensitively()
    {
        List<CleanupTarget> allowed =
        [
            Target("revit-journals", "Revit journals"),
            Target("collab-cache", "Collaboration cache"),
            Target("pac-cache", "PAC cache", enabled: false),
        ];
        var picked = HealthService.SelectTargets(allowed, ["REVIT-JOURNALS", "pac-cache", "not-a-target", @"C:\Windows"]);
        Assert.Equal(["revit-journals"], picked.Select(t => t.Id));
    }

    [Fact]
    public void SelectTargets_MatchesByName_WhenATargetHasNoId()
    {
        List<CleanupTarget> allowed = [Target("", "Legacy cache"), Target("journals", "Journals")];
        var picked = HealthService.SelectTargets(allowed, ["legacy cache"]);
        Assert.Equal(["Legacy cache"], picked.Select(t => t.Name));
        // A named target with an id is matched by its id, not its name.
        Assert.Empty(HealthService.SelectTargets(allowed, ["Journals2", "Journals "]));
        Assert.Single(HealthService.SelectTargets(allowed, ["journals"]));
    }

    [Fact]
    public void SelectTargets_NothingSelected_SelectsNothing() =>
        Assert.Empty(HealthService.SelectTargets([Target("a", "A")], []));
}

/// <summary>The process-wide flag is static: these tests run one at a time.</summary>
[Collection(Name)]
public class HealthWorkTests
{
    public const string Name = "Health work flag";

    [Fact]
    public void OneScanOrCleanAtATime_AndFinishedIsRaisedWhenItEnds()
    {
        var finished = 0;
        EventHandler handler = (_, _) => finished++;
        HealthWork.Finished += handler;
        try
        {
            Assert.False(HealthWork.IsRunning);
            var work = HealthWork.Begin();
            Assert.True(HealthWork.IsRunning);
            var service = new HealthService(null);
            Assert.True(service.IsWorkRunning);

            // A reopened window's scan or clean is refused while the first one runs, before it touches anything.
            Assert.Throws<InvalidOperationException>(() => HealthWork.Begin());
            Assert.Throws<InvalidOperationException>(() => service.Clean(["revit-journals"]));
            Assert.Throws<InvalidOperationException>(() => service.RunScan());
            Assert.Equal(0, finished);

            work.Dispose();
            work.Dispose(); // idempotent
            Assert.False(HealthWork.IsRunning);
            Assert.False(service.IsWorkRunning);
            Assert.Equal(1, finished);

            using (HealthWork.Begin())
                Assert.True(HealthWork.IsRunning);
            Assert.False(HealthWork.IsRunning);
        }
        finally
        {
            HealthWork.Finished -= handler;
        }
    }
}
