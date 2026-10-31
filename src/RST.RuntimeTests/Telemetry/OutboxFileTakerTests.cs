// rst-c's own (not vendored): the Windows half of the activity cleanup's file
// taking (spec #24 Cleanup; PR #177 review R2). RST.Tests covers the portable
// path on Linux; this runs the real Windows handle path on windows-latest: a
// taken file cannot be written, renamed or replaced, a live writer's file
// cannot be taken, and the delete goes through the handle.

using System.IO;
using RST.Core.Telemetry.Rollups;
using Xunit;

namespace RST.RuntimeTests.Telemetry;

public sealed class OutboxFileTakerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RST-Taker-" + Guid.NewGuid().ToString("N"));

    public OutboxFileTakerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void A_taken_file_cannot_be_written_renamed_or_replaced_and_is_deleted_through_the_handle()
    {
        var path = Write("a.jsonl", "original\n");
        var replacement = Write("b.jsonl", "replacement, longer\n");

        using (var taken = OutboxFileTaker.Default.TryTake(path))
        {
            Assert.NotNull(taken);
            Assert.Equal(new FileInfo(path).Length, taken!.Length);
            Assert.Equal("original\n", System.Text.Encoding.UTF8.GetString(taken.ReadAll()));

            Assert.ThrowsAny<Exception>(() => File.AppendAllText(path, "more"));
            Assert.ThrowsAny<Exception>(() => File.Move(path, path + ".moved"));
            Assert.ThrowsAny<Exception>(() => File.Move(replacement, path, overwrite: true));

            Assert.True(taken.Delete());
        }

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(replacement), "only the held file is deleted");
    }

    [Fact]
    public void A_file_a_live_writer_holds_cannot_be_taken()
    {
        var path = Write("live.jsonl", "x\n");
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        Assert.Null(OutboxFileTaker.Default.TryTake(path));
    }

    [Fact]
    public void A_taken_file_that_is_not_deleted_stays()
    {
        var path = Write("keep.jsonl", "keep\n");
        using (var taken = OutboxFileTaker.Default.TryTake(path)) Assert.NotNull(taken);
        Assert.True(File.Exists(path));
    }
}
