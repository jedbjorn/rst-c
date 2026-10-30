// rst-c's own (not vendored): feature #18 (spec #19), WU1 review F6. A profile read from a file
// with no id gets an id and a canonical file name on its first save; the service then removes the
// file it was read from, so the profile is not listed twice. Only a bare .json name in the profiles
// folder is removed; one already gone is fine; a failure is reported, not thrown.

using System.IO;
using FluentAssertions;
using RST.Core.Builder;
using RST.Core.Profiles;
using Xunit;

namespace RST.Tests.Builder;

public sealed class ProfileSaveSupersededFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rst-superseded-" + Guid.NewGuid().ToString("N"));

    public ProfileSaveSupersededFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static Profile IdLess() => new()
    {
        Id = null,
        ProfileName = "Legacy",
        Tab = "Studio",
        ExportDate = "2024-05-01",
        Panels = [new Panel { Name = "P", Color = "#4f8ef7", Slots = [new Slot { SlotType = "tool", Name = "Wall", CommandId = "ID_WALL" }] }],
    };

    private string[] Files() => Directory.GetFiles(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    [Fact]
    public void The_first_save_of_an_id_less_profile_then_the_removal_leaves_one_file_with_an_id()
    {
        using (var fs = File.Create(Path.Combine(_dir, "Legacy.json")))
            ProfileSerializer.Write(IdLess(), fs);
        var entry = ProfileStore.List(_dir).Single();
        entry.Profile.Id.Should().BeNullOrEmpty();

        var service = new ProfileSaveService(_dir);
        var saved = service.Save(ProfileEditing.ForEditing(entry.Profile));
        saved.Ok.Should().BeTrue();
        Files().Should().HaveCount(2, "the save cannot match the old file by id");

        service.RemoveSupersededFile(entry.FileName).Ok.Should().BeTrue();
        Files().Should().Equal(saved.FileName);
        ProfileStore.List(_dir).Single().Profile.Id.Should().Be(saved.Saved!.Id);

        service.RemoveSupersededFile(entry.FileName).Ok.Should().BeTrue("a file already gone is fine");
    }

    [Theory]
    [InlineData(@"..\outside.json")]
    [InlineData("sub/inner.json")]
    [InlineData("notes.txt")]
    [InlineData("")]
    public void Only_a_bare_profile_file_name_is_removed(string fileName)
    {
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "x");
        new ProfileSaveService(_dir).RemoveSupersededFile(fileName).Ok.Should().BeFalse();
        File.Exists(Path.Combine(_dir, "notes.txt")).Should().BeTrue();
    }
}
