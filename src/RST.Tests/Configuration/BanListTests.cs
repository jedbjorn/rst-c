using System.IO;
using FluentAssertions;
using RST.Core.Configuration;
using Xunit;

namespace RST.Tests.Configuration;

public sealed class BanListTests
{
    [Fact]
    public void Empty_HasZeroEntries()
    {
        var list = BanList.Empty();
        list.Count.Should().Be(0);
        list.IsBanned("anything").Should().BeFalse();
    }

    [Fact]
    public void Add_NewId_ReturnsTrueAndIsBanned()
    {
        var list = BanList.Empty();
        list.Add("ID_TEST").Should().BeTrue();
        list.IsBanned("ID_TEST").Should().BeTrue();
    }

    [Fact]
    public void Add_DuplicateId_ReturnsFalse()
    {
        var list = BanList.Empty();
        list.Add("ID_TEST");
        list.Add("ID_TEST").Should().BeFalse();
        list.Count.Should().Be(1);
    }

    [Fact]
    public void Remove_RemovesId()
    {
        var list = BanList.Empty();
        list.Add("ID_TEST");
        list.Remove("ID_TEST").Should().BeTrue();
        list.IsBanned("ID_TEST").Should().BeFalse();
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"banlist-missing-{Path.GetRandomFileName()}.json");
        var list = BanList.Load(path);
        list.Count.Should().Be(0);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"banlist-corrupt-{Path.GetRandomFileName()}.json");
        File.WriteAllText(path, "{ this is not valid json");
        try
        {
            var list = BanList.Load(path);
            list.Count.Should().Be(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RoundTrip_PreservesEntries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"banlist-roundtrip-{Path.GetRandomFileName()}.json");
        try
        {
            var written = BanList.Empty();
            written.Add("ID_FIRST");
            written.Add("CustomCtrl_%CustomCtrl_%Foo%Bar%Baz");
            written.Save(path);

            var read = BanList.Load(path);
            read.Count.Should().Be(2);
            read.IsBanned("ID_FIRST").Should().BeTrue();
            read.IsBanned("CustomCtrl_%CustomCtrl_%Foo%Bar%Baz").Should().BeTrue();
            read.IsBanned("ID_NOT_PRESENT").Should().BeFalse();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_FileWithComments_ParsesIds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"banlist-comments-{Path.GetRandomFileName()}.json");
        File.WriteAllText(path, """
            {
              // Top-level comment, admin notes
              "version": 1,
              "bannedIds": [
                "ID_A", // why this one is banned
                "ID_B"
              ]
            }
            """);
        try
        {
            var list = BanList.Load(path);
            list.IsBanned("ID_A").Should().BeTrue();
            list.IsBanned("ID_B").Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_CreatesParentDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"banlist-dir-{Path.GetRandomFileName()}");
        var path = Path.Combine(dir, "bans.json");
        try
        {
            var list = BanList.Empty();
            list.Add("ID_X");
            list.Save(path);
            File.Exists(path).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
