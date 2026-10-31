// rst-c's own (not vendored). SC-046 review N3: a command's guard keeps the Builder running after a failure, so a
// half-done switch must not leave state a later Save acts on. Switching to a profile read from an id-less file
// records that file for the first save to replace (WU1 review F6); a switch that fails must not leave it recorded
// for whatever the editor holds instead, or that Save removes a file that is not the editor's own.

using System.Collections;
using FluentAssertions;
using RST.Core.Profiles;
using RST.RuntimeTests.Loader;
using Xunit;

namespace RST.RuntimeTests.Builder;

public sealed class ProfileBuilderHardeningTests
{
    /// <summary>The ribbon's tabs, whose listing fails (the editor reads them part-way through loading a profile).</summary>
    private sealed class FailingTabs : IReadOnlyList<string>
    {
        public int Count => 0;

        public string this[int index] => throw new InvalidOperationException("SC-046 test: the tabs could not be read");

        public IEnumerator<string> GetEnumerator() => throw new InvalidOperationException("SC-046 test: the tabs could not be read");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static FakeBuilderBackend WithLegacy()
    {
        var backend = BuilderFixtures.Backend();
        var legacy = LoaderFixtures.Structure();
        legacy.Id = null;
        legacy.ProfileName = "Legacy";
        backend.Library.Add(new ProfileEntry("Legacy.json", legacy));
        return backend;
    }

    [Fact]
    public async Task A_switch_that_fails_part_way_leaves_nothing_for_the_next_save_to_remove()
    {
        var backend = WithLegacy();
        var h = BuilderHarness.Create(backend);
        h.Model.SelectedProfileOption = h.Option("Structure");

        backend.AllTabs = new FailingTabs(); // read after the editor took the profile, before the load completes
        h.Model.SwitchProfileCommand.Execute(h.Option("Legacy"));
        backend.AllTabs = [];

        h.Model.MinVersion = "3.1.0";
        (await h.Model.SaveAsync()).Should().BeTrue();
        h.Backend.Removed.Should().BeEmpty("a half-done load records no file to replace");
    }

    [Fact]
    public async Task A_switch_that_completes_still_replaces_the_id_less_file_on_the_first_save()
    {
        var h = BuilderHarness.Create(WithLegacy());
        h.Model.SwitchProfileCommand.Execute(h.Option("Legacy"));
        h.Model.Profile.ProfileName.Should().Be("Legacy");

        h.Model.MinVersion = "3.1.0";
        (await h.Model.SaveAsync()).Should().BeTrue();
        h.Backend.Removed.Should().Equal("Legacy.json");
    }
}
