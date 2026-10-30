// rst-c's own (not vendored): the live log survives Health's clean-up (decision #33, review B1). The file
// sink opens its file without FileShare.Delete, as Serilog did, so on Windows File.Delete on the open log
// fails and HealthCleaner.PurgeFlat skips it as in use, while it deletes the closed logs beside it. Only a
// Windows runner can show this: on Linux an open file can always be unlinked.

using System.IO;
using RST.Core.Logging;
using RST.UI.Health;
using Xunit;

namespace RST.RuntimeTests.Health;

public sealed class LiveLogCleanupTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, object?> NoProps = new Dictionary<string, object?>();

    private readonly string _logs = Path.Combine(Path.GetTempPath(), "RST-LiveLog-" + Guid.NewGuid().ToString("N"));

    public LiveLogCleanupTests() => Directory.CreateDirectory(_logs);

    public void Dispose()
    {
        try { Directory.Delete(_logs, recursive: true); }
        catch { /* best effort */ }
    }

    private static LogEvent Event(string text) => new(DateTimeOffset.Now, LogLevel.Information, text, Array.Empty<object?>(), null);

    private static string ReadShared(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public static TheoryData<bool> Modes => new() { false, true };

    [Theory]
    [MemberData(nameof(Modes))]
    public void The_open_log_cannot_be_deleted_and_the_cleanup_skips_it_while_deleting_closed_logs(bool shared)
    {
        // The engine's session log (not shared) and the tray's day file (shared), beside two closed logs.
        var older = new[] { "rst_2026-10-29_080000.log", "rst_2026-10-29_080000_boot.log" };
        foreach (var f in older) File.WriteAllText(Path.Combine(_logs, f), "old");
        var options = shared
            ? new FileSinkOptions
            {
                Path = Path.Combine(_logs, "tray_.log"),
                OutputTemplate = "{Message}{NewLine}",
                RollingInterval = RollingInterval.Day,
                RetainedFileCountLimit = 10,
                FileSizeLimitBytes = 5 * 1024 * 1024,
                RollOnFileSizeLimit = true,
                Shared = true,
            }
            : new FileSinkOptions { Path = Path.Combine(_logs, "rst_2026-10-30_090000.log"), OutputTemplate = "{Message}{NewLine}" };

        using (var sink = new FileSink(options))
        {
            sink.Emit(Event("before the clean-up"), NoProps);
            var live = sink.CurrentPath!;

            Assert.Throws<IOException>(() => File.Delete(live));

            var (deleted, skipped) = HealthCleaner.PurgeFlat(_logs, "RST Logs");
            Assert.Equal(older.Length, deleted);
            Assert.Equal(1, skipped);
            Assert.True(File.Exists(live), "the live log is still there");
            Assert.Equal(new[] { Path.GetFileName(live) }, Directory.GetFiles(_logs).Select(Path.GetFileName));

            sink.Emit(Event("after the clean-up"), NoProps);
            Assert.Equal("before the clean-up" + Environment.NewLine + "after the clean-up" + Environment.NewLine, ReadShared(live));
        }
    }
}
