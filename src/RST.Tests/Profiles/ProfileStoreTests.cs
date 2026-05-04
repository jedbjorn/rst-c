// ProfileStoreTests.cs — round-trip + resolution behaviour for ProfileStore.

using System.IO;
using FluentAssertions;
using RST.Core.Profiles;
using Xunit;

namespace RST.Tests.Profiles;

public sealed class ProfileStoreTests
{
    private static Profile MakeProfile(string name, string id, string date = "2026-05-04") =>
        new()
        {
            SchemaVersion = ProfileSerializer.CurrentSchemaVersion,
            Id = id,
            ProfileName = name,
            Tab = name,
            ExportDate = date,
            Panels =
            {
                new Panel
                {
                    Name = "P",
                    Color = "#4f8ef7",
                    Slots = { new Slot { SlotType = "tool", Name = "Move", CommandId = "ID_BUTTON_MOVE" } }
                }
            }
        };

    [Fact]
    public void Save_then_List_round_trips_and_uses_canonical_filename()
    {
        using var dir = new TempDir();
        var fileName = ProfileStore.Save(MakeProfile("My Studio", "id-1"), dir.Path);

        fileName.Should().Be("My_Studio_2026-05-04.json");
        File.Exists(Path.Combine(dir.Path, fileName)).Should().BeTrue();

        var listed = ProfileStore.List(dir.Path);
        listed.Should().ContainSingle()
            .Which.Profile.Id.Should().Be("id-1");
    }

    [Fact]
    public void Resolve_prefers_id_over_name()
    {
        using var dir = new TempDir();
        ProfileStore.Save(MakeProfile("Same", "id-A"), dir.Path);
        ProfileStore.Save(MakeProfile("Same", "id-B", date: "2026-05-05"), dir.Path);

        var resolved = ProfileStore.Resolve("Same", "id-B", dir.Path);
        resolved.Should().NotBeNull();
        resolved!.Profile.Id.Should().Be("id-B");
    }

    [Fact]
    public void Save_replaces_existing_entry_with_same_id_under_new_filename()
    {
        using var dir = new TempDir();
        ProfileStore.Save(MakeProfile("X", "id-1", date: "2026-05-04"), dir.Path);
        ProfileStore.Save(MakeProfile("X", "id-1", date: "2026-05-10"), dir.Path);

        var files = Directory.GetFiles(dir.Path, "*.json");
        files.Should().ContainSingle()
            .Which.Should().EndWith("_2026-05-10.json");
    }

    [Fact]
    public void Delete_removes_file()
    {
        using var dir = new TempDir();
        var fn = ProfileStore.Save(MakeProfile("Y", "id-y"), dir.Path);
        ProfileStore.Delete(fn, dir.Path).Should().BeTrue();
        Directory.GetFiles(dir.Path, "*.json").Should().BeEmpty();
    }

    [Fact]
    public void List_skips_unparseable_files_without_throwing()
    {
        using var dir = new TempDir();
        ProfileStore.Save(MakeProfile("Good", "id-good"), dir.Path);
        File.WriteAllText(Path.Combine(dir.Path, "broken.json"), "{not json");

        ProfileStore.List(dir.Path)
            .Should().ContainSingle()
            .Which.Profile.ProfileName.Should().Be("Good");
    }

    [Fact]
    public void Save_assigns_id_when_missing()
    {
        using var dir = new TempDir();
        var p = MakeProfile("Z", id: null!);
        p.Id = null;
        ProfileStore.Save(p, dir.Path);
        p.Id.Should().NotBeNullOrEmpty();
    }

    private sealed class TempDir : System.IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rst-tests-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
