// rst-c's own: the in-house logger's file sink and static facade (decision #33). The file names, rolling,
// size limits and retention are the ones the Serilog.Sinks.File configurations had: the engine's single
// rst_<ts>.log per session, and the tray's tray_<yyyyMMdd>[_NNN].log, rolled daily and at 5 MB, ten kept.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RST.Core.Configuration;
using RST.Core.Logging;
using RST.Core.Tray;
using Xunit;

namespace RST.Tests.Logging;

public sealed class FileSinkTests : IDisposable
{
    private const string EngineTemplate = "{Timestamp:HH:mm:ss.fff} {Level:u3} {Message:lj}{NewLine}{Exception}";
    private const string TrayTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3} [{ProcessId}] {Message:lj}{NewLine}{Exception}";

    private static readonly IReadOnlyDictionary<string, object?> NoProps = new Dictionary<string, object?>();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RST-Logging-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(new DateTime(2026, 10, 30, 9, 0, 0, DateTimeKind.Local));

    public FileSinkTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    private LogEvent Event(string template, params object?[] values) => new(_now, LogLevel.Information, template, values, null);

    private FileSink Tray(long limit = 5 * 1024 * 1024) => new(new FileSinkOptions
    {
        Path = Path.Combine(_dir, TrayPaths.LogFileBaseName),
        OutputTemplate = TrayTemplate,
        RollingInterval = RollingInterval.Day,
        RetainedFileCountLimit = TrayPaths.LogFilesKept,
        FileSizeLimitBytes = limit,
        RollOnFileSizeLimit = true,
        Shared = true,
        Now = () => _now,
    });

    private string[] Files(string pattern = "*") =>
        Directory.GetFiles(_dir, pattern).Select(p => Path.GetFileName(p)!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static string ReadShared(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    [Fact]
    public void The_engine_session_log_is_one_file_at_the_given_path_in_utf8_without_a_bom()
    {
        var path = Path.Combine(_dir, "rst_2026-10-30_090000.log");
        using (var sink = new FileSink(new FileSinkOptions { Path = path, OutputTemplate = EngineTemplate }))
        {
            sink.Emit(Event("=== RST {Version} starting on Revit {RevitVersion} ===", "3.1.0.0", "2026"), NoProps);
            sink.Emit(Event("Paths: appData={AppData}", @"C:\Users\u\AppData\Roaming\RST"), NoProps);
            // Readable while open (a tail, Health's cleanup check).
            ReadShared(path).Should().Contain("starting on Revit 2026");
        }

        Files().Should().Equal("rst_2026-10-30_090000.log");
        var bytes = File.ReadAllBytes(path);
        bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF });
        File.ReadAllText(path).Should().Be(
            "09:00:00.000 INF === RST 3.1.0.0 starting on Revit 2026 ===" + Environment.NewLine +
            @"09:00:00.000 INF Paths: appData=C:\Users\u\AppData\Roaming\RST" + Environment.NewLine);
    }

    [Fact]
    public void A_full_file_that_does_not_roll_takes_no_more_events()
    {
        var path = Path.Combine(_dir, "rst_full.log");
        using (var sink = new FileSink(new FileSinkOptions { Path = path, OutputTemplate = "{Message}{NewLine}", FileSizeLimitBytes = 10 }))
        {
            sink.Emit(Event("first line"), NoProps);   // 0 bytes before: written, now past the limit
            sink.Emit(Event("second line"), NoProps);  // at the limit: dropped (Serilog's rule)
        }

        File.ReadAllText(path).Should().Be("first line" + Environment.NewLine);
        Files().Should().Equal("rst_full.log");
    }

    [Fact]
    public void The_tray_log_is_named_after_the_day_and_rolls_at_midnight()
    {
        using (var sink = Tray())
        {
            sink.Emit(Event("RST tray {Version} starting", "3.1.0"), new Dictionary<string, object?> { ["ProcessId"] = 7 });
            _now = new DateTimeOffset(new DateTime(2026, 10, 30, 23, 59, 59, DateTimeKind.Local));
            sink.Emit(Event("late"), NoProps);
            _now = _now.AddSeconds(2);
            sink.Emit(Event("next day"), NoProps);
            sink.CurrentPath.Should().EndWith(DayName(_now));
        }

        Files().Should().Equal(DayName(new DateTime(2026, 10, 30)), DayName(new DateTime(2026, 10, 31)));
        ReadShared(Path.Combine(_dir, DayName(new DateTime(2026, 10, 30))))
            .Should().StartWith("2026-10-30 09:00:00.000 INF [7] RST tray 3.1.0 starting")
            .And.Contain("late").And.NotContain("next day");
        ReadShared(Path.Combine(_dir, DayName(new DateTime(2026, 10, 31)))).Should().StartWith("2026-10-31 00:00:01.000 INF [] next day");
    }

    private static string DayName(DateTimeOffset d) => DayName(d.LocalDateTime);

    private static string DayName(DateTime local) => "tray_" + local.ToString("yyyyMMdd") + ".log";

    [Fact]
    public void Past_the_size_limit_the_tray_log_rolls_to_the_next_sequence_number()
    {
        var day = _now.LocalDateTime.ToString("yyyyMMdd");
        using (var sink = Tray(limit: 60))
        {
            for (var i = 0; i < 5; i++) sink.Emit(Event("line {N} padded to forty bytes or so ....", i), NoProps);
        }

        Files().Should().Equal($"tray_{day}.log", $"tray_{day}_001.log", $"tray_{day}_002.log", $"tray_{day}_003.log", $"tray_{day}_004.log");
        foreach (var f in Files()) File.ReadAllText(Path.Combine(_dir, f)).Should().Contain("padded");
    }

    [Fact]
    public void A_restarted_tray_appends_to_the_days_highest_sequence()
    {
        var day = _now.LocalDateTime.ToString("yyyyMMdd");
        File.WriteAllText(Path.Combine(_dir, $"tray_{day}.log"), "old" + Environment.NewLine);
        File.WriteAllText(Path.Combine(_dir, $"tray_{day}_002.log"), "older roll" + Environment.NewLine);

        using (var sink = Tray()) sink.Emit(Event("again"), NoProps);

        File.ReadAllText(Path.Combine(_dir, $"tray_{day}_002.log")).Should().StartWith("older roll").And.Contain("again");
        File.ReadAllText(Path.Combine(_dir, $"tray_{day}.log")).Should().Be("old" + Environment.NewLine);
    }

    [Fact]
    public void Retention_keeps_the_newest_ten_tray_files_and_never_touches_other_files()
    {
        // 14 older tray files, the engine's session and boot logs, and two names outside the pattern.
        var start = new DateTime(2026, 10, 1);
        for (var i = 0; i < 14; i++) File.WriteAllText(Path.Combine(_dir, DayName(start.AddDays(i))), "x");
        string[] others = { "rst_2026-10-01_101500.log", "rst_2026-10-01_101500_boot.log", "tray_notes.log", "tray_.txt" };
        foreach (var f in others) File.WriteAllText(Path.Combine(_dir, f), "keep");

        using (var sink = Tray()) sink.Emit(Event("today"), NoProps);

        var tray = Files(TrayPaths.LogFileGlob).Where(f => f != "tray_notes.log").ToArray();
        tray.Should().HaveCount(TrayPaths.LogFilesKept, "the current file counts toward the ten");
        tray.Should().Contain(DayName(_now));
        tray.Should().NotContain(DayName(start.AddDays(4)), "the oldest go first").And.Contain(DayName(start.AddDays(5)));
        foreach (var f in others) File.Exists(Path.Combine(_dir, f)).Should().BeTrue($"{f} is not the tray's");
        Files(AppDataPaths.EngineSessionLogGlob).Should().HaveCount(2);
    }

    [Fact]
    public void Two_shared_writers_append_to_one_file_without_losing_lines()
    {
        using (var a = Tray())
        using (var b = Tray())
        {
            for (var i = 0; i < 50; i++)
            {
                a.Emit(Event("a {N}", i), NoProps);
                b.Emit(Event("b {N}", i), NoProps);
            }
        }

        var lines = ReadShared(Path.Combine(_dir, DayName(_now))).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(100);
        lines.Where(l => l.EndsWith(" a 49", StringComparison.Ordinal)).Should().ContainSingle();
        lines.Where(l => l.EndsWith(" b 49", StringComparison.Ordinal)).Should().ContainSingle();
    }

    [Fact]
    public void The_shared_mutex_is_session_local_and_one_per_file()
    {
        var a = FileSink.SharedMutexName(Path.Combine(_dir, "tray_.log"));
        a.Should().StartWith(@"Local\RST.Log.", "review B3: never Global\\, which another user could hold");
        a.Should().Be(FileSink.SharedMutexName(Path.Combine(_dir, "TRAY_.LOG")));
        a.Should().NotBe(FileSink.SharedMutexName(Path.Combine(_dir, "other_.log")));
    }

    [Fact]
    public void A_stuck_mutex_holder_costs_one_dropped_event_then_writes_skip_the_mutex_for_a_while()
    {
        var path = Path.Combine(_dir, TrayPaths.LogFileBaseName);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var m = new Mutex(false, FileSink.SharedMutexName(Path.GetFullPath(path)));
            m.WaitOne();
            held.Set();
            release.Wait();
            m.ReleaseMutex();
        });
        holder.Start();
        held.Wait();
        try
        {
            using var sink = new FileSink(new FileSinkOptions
            {
                Path = path,
                OutputTemplate = "{Message}{NewLine}",
                RollingInterval = RollingInterval.Day,
                Shared = true,
                SharedWaitTimeout = TimeSpan.FromMilliseconds(200),
                SharedBackoff = TimeSpan.FromMinutes(1),
                Now = () => _now,
            });
            sink.Emit(Event("dropped after the wait"), NoProps);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            sink.Emit(Event("written without waiting"), NoProps);
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(150), "the back-off skips the mutex");
            _now = _now.AddMinutes(2);
            sink.Emit(Event("waits again after the back-off"), NoProps); // dropped: still held
            ReadShared(Path.Combine(_dir, DayName(_now))).Should().Be("written without waiting" + Environment.NewLine);
        }
        finally
        {
            release.Set();
            holder.Join();
        }
    }

    [Fact]
    public void Concurrent_writes_from_many_threads_keep_every_line_whole()
    {
        var path = Path.Combine(_dir, "rst_threads.log");
        using (var sink = new FileSink(new FileSinkOptions { Path = path, OutputTemplate = "{Message}{NewLine}" }))
        {
            var logger = new Logger(LogLevel.Debug, sink);
            Parallel.For(0, 8, t =>
            {
                for (var i = 0; i < 250; i++) logger.Write(LogLevel.Information, null, "thread {T} line {I} end", new object?[] { t, i });
            });
        }

        var lines = File.ReadAllLines(path);
        lines.Should().HaveCount(2000);
        lines.Should().OnlyContain(l => l.StartsWith("thread ", StringComparison.Ordinal) && l.EndsWith(" end", StringComparison.Ordinal));
        lines.Distinct().Should().HaveCount(2000);
    }

    [Fact]
    public void An_unwritable_path_never_throws()
    {
        var blocker = Path.Combine(_dir, "not-a-folder");
        File.WriteAllText(blocker, "x");
        var act = () =>
        {
            using var sink = new FileSink(new FileSinkOptions { Path = Path.Combine(blocker, "rst_x.log") });
            sink.Emit(Event("dropped"), NoProps);
            sink.CurrentPath.Should().BeNull();
        };
        act.Should().NotThrow();
    }

    [Fact]
    public void After_dispose_events_are_dropped()
    {
        var path = Path.Combine(_dir, "rst_closed.log");
        var sink = new FileSink(new FileSinkOptions { Path = path, OutputTemplate = "{Message}{NewLine}" });
        sink.Emit(Event("kept"), NoProps);
        sink.Dispose();
        sink.Emit(Event("dropped"), NoProps);
        sink.Dispose();

        File.ReadAllText(path).Should().Be("kept" + Environment.NewLine);
    }
}

[Collection(LogFacadeTests.Collection)]
public sealed class LogFacadeTests
{
    /// <summary>The tests swap the process-wide Log.Logger, so they run one at a time.</summary>
    public const string Collection = "RST.Core.Logging.Log";

    private sealed class ListSink : ILogSink
    {
        public List<LogEvent> Events { get; } = new();
        public List<IReadOnlyDictionary<string, object?>> Properties { get; } = new();
        public bool Disposed { get; private set; }
        public bool Throw { get; set; }

        public void Emit(LogEvent logEvent, IReadOnlyDictionary<string, object?> properties)
        {
            if (Throw) throw new IOException("disk full");
            lock (Events)
            {
                Events.Add(logEvent);
                Properties.Add(properties);
            }
        }

        public void Dispose() => Disposed = true;
    }

    private static void With(ListSink sink, LogLevel minimum, Action body, IReadOnlyDictionary<string, object?>? props = null)
    {
        var previous = Log.Logger;
        Log.Logger = new Logger(minimum, new ILogSink[] { sink }, props);
        try { body(); }
        finally { Log.Logger = previous; }
    }

    [Fact]
    public void Each_level_and_overload_reaches_the_sink_with_its_exception_and_arguments()
    {
        var sink = new ListSink();
        var ex = new InvalidOperationException("boom");
        With(sink, LogLevel.Verbose, () =>
        {
            Log.Verbose("v {A}", 1);
            Log.Debug(ex, "d {A} {B}", 1, 2);
            Log.Information("i {A} {B} {C}", 1, 2, 3);
            Log.Information("i4 {A} {B} {C} {D}", 1, 2, 3, 4);
            Log.Warning(ex, "w");
            Log.Error(ex, "e {A}", "x");
            Log.Fatal("f");
        });

        sink.Events.Select(e => (e.Level, e.RenderMessage(), e.Exception)).Should().Equal(
            (LogLevel.Verbose, "v 1", (Exception?)null),
            (LogLevel.Debug, "d 1 2", ex),
            (LogLevel.Information, "i 1 2 3", null),
            (LogLevel.Information, "i4 1 2 3 4", null),
            (LogLevel.Warning, "w", ex),
            (LogLevel.Error, "e x", ex),
            (LogLevel.Fatal, "f", null));
    }

    [Fact]
    public void A_single_array_argument_is_one_property()
    {
        var sink = new ListSink();
        With(sink, LogLevel.Debug, () =>
        {
            Log.Information("tabs={Tabs}", new[] { "Docs", "Arch" });
            object?[] args = { "a", 2 };
            Log.Information("{A} {B}", args); // an object[] is the argument list, as in Serilog
        });

        sink.Events.Select(e => e.RenderMessage()).Should().Equal("tabs=[\"Docs\",\"Arch\"]", "a 2");
    }

    [Fact]
    public void Events_below_the_minimum_level_are_dropped_and_properties_are_attached()
    {
        var sink = new ListSink();
        With(sink, LogLevel.Information, () =>
        {
            Log.Debug("hidden");
            Log.IsEnabled(LogLevel.Debug).Should().BeFalse();
            Log.Information("shown");
        }, new Dictionary<string, object?> { ["Component"] = "RST.Engine" });

        sink.Events.Should().ContainSingle().Which.RenderMessage().Should().Be("shown");
        sink.Properties[0]["Component"].Should().Be("RST.Engine");
    }

    [Fact]
    public void A_throwing_sink_never_reaches_the_caller()
    {
        var sink = new ListSink { Throw = true };
        var act = () => With(sink, LogLevel.Debug, () =>
        {
            Log.Error(new Exception("x"), "still fine {A}", 1);
            Log.Information(null!, 1);
        });
        act.Should().NotThrow();
    }

    [Fact]
    public void CloseAndFlush_disposes_the_logger_and_later_calls_are_dropped()
    {
        var sink = new ListSink();
        var previous = Log.Logger;
        try
        {
            Log.Logger = new Logger(LogLevel.Debug, sink);
            Log.Information("before");
            Log.CloseAndFlush();
            Log.Information("after");
            Log.CloseAndFlush();

            sink.Disposed.Should().BeTrue();
            sink.Events.Select(e => e.RenderMessage()).Should().Equal("before");
            Log.Logger.Should().BeSameAs(Logger.None);
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    [Fact]
    public void Before_configuration_every_call_is_a_no_op()
    {
        var previous = Log.Logger;
        try
        {
            Log.Logger = null!;
            Log.Logger.Should().BeSameAs(Logger.None);
            var act = () => Log.Fatal(new Exception("x"), "nobody listens {A}", 1);
            act.Should().NotThrow();
        }
        finally
        {
            Log.Logger = previous;
        }
    }
}

[CollectionDefinition(LogFacadeTests.Collection, DisableParallelization = true)]
public sealed class LogFacadeCollection
{
}
