// rst-c's own (not vendored): HealthService's guards (spec #16, WU2 review N2, N5): one scan or
// clean at a time in the process, and the cleanup id re-check that keeps a clean to the active
// profile's enabled targets. Also its activity read (spec #24, Revit Health "Data path"): the
// current model's identity from the Revit context, the rollup read path over the outbox with this
// Revit session's live file, and nothing written.

using System.IO;
using RST.Core.Health;
using RST.Core.Profiles;
using RST.Core.Telemetry;
using RST.Core.Telemetry.Rollups;
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

public sealed class HealthServiceActivityTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 14, 0, 0, TimeSpan.Zero);
    private const string CentralPath = @"\\server\projects\Tower.rvt";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RST-HealthActivity-" + Guid.NewGuid().ToString("N"));

    private string Outbox => Path.Combine(_root, "outbox");

    private string Rollups => Path.Combine(_root, "rollups");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private static DocumentIdentity Tower => new()
    {
        CreationGuid = "6b1f2c4e-0000-4000-8000-000000000001",
        CentralPath = CentralPath,
        LocalPath = @"C:\Users\me\Documents\Tower_me.rvt",
        Title = "Tower",
        IsWorkshared = true,
        IsCloud = false,
    };

    /// <summary>A session file: open the model, a pulse a minute for <paramref name="minutes"/>, and optionally close and end.</summary>
    private void Session(string guid, DateTimeOffset start, int minutes, bool closed)
    {
        long seq = 0;
        var lines = new List<TelemetryEvent>();
        TelemetryEvent Add(string type, DateTimeOffset ts)
        {
            var e = new TelemetryEvent { EventId = Guid.NewGuid().ToString(), SessionGuid = guid, Seq = ++seq, Ts = ts, EventType = type };
            lines.Add(e);
            return e;
        }
        Add(TelemetryEventTypes.SessionStart, start).SetField(TelemetryFields.RevitVersion, "2026");
        Tower.WriteTo(Add(TelemetryEventTypes.DocOpened, start.AddMinutes(1)));
        for (var m = 0; m < minutes; m++) Tower.WriteKeysTo(Add(TelemetryEventTypes.DocChangedPulse, start.AddMinutes(2 + m)));
        if (closed)
        {
            Tower.WriteKeysTo(Add(TelemetryEventTypes.DocClosing, start.AddMinutes(3 + minutes)));
            Tower.WriteKeysTo(Add(TelemetryEventTypes.DocClosed, start.AddMinutes(3 + minutes)));
            Add(TelemetryEventTypes.SessionEnd, start.AddMinutes(4 + minutes));
        }
        Directory.CreateDirectory(Outbox);
        File.WriteAllText(Path.Combine(Outbox, OutboxFiles.SessionFileName("install-1", guid)),
            string.Concat(lines.Select(e => TelemetryJson.SerializeLine(e) + "\n")));
    }

    private HealthService Service(HealthContext context) => new(context, readOptions: () => new ActivityReadOptions
    {
        OutboxDir = Outbox,
        RollupsDir = Rollups,
        Zone = TimeZoneInfo.Utc,
        NowUtc = Now,
    });

    [Fact]
    public void GetActivity_ReadsTheCurrentModel_WithThisRevitSessionLive_AndWritesNothing()
    {
        var live = Guid.NewGuid().ToString();
        Session(Guid.NewGuid().ToString(), Now.AddDays(-2), 20, closed: true); // not folded by the tray yet
        Session(live, Now.AddMinutes(-30), 11, closed: false);                   // this Revit session, still open
        var context = new HealthContext
        {
            ModelName = "Tower", CreationGuid = Tower.CreationGuid, CentralPath = CentralPath,
            IsWorkshared = true, IsCloud = false, WarningsCount = 12, ModelSizeMb = 2, TelemetrySessionGuid = live,
        };

        var data = Service(context).GetActivity();

        var model = Assert.IsType<ModelActivity>(data.Model);
        Assert.Equal(ActivityMatchKinds.CentralPath, model.MatchedKeyKind);
        Assert.Equal(11 * 60, model.SessionActiveSeconds);                       // Activity (Session)
        Assert.Equal(31 * 60, model.Days.Sum(d => d.ActiveSeconds));             // both files: Activity (Total) source
        Assert.Equal(Now.AddMinutes(-29), model.LiveOpenSinceUtc);
        Assert.Equal(12, data.WarningsCount);
        Assert.Equal(2d * 1024 * 1024, data.FileSizeBytes);
        Assert.True(data.IsWorkshared);
        Assert.Equal(2, data.Status.OutboxFileCount);
        Assert.False(Directory.Exists(Rollups), "Revit only reads; the tray writes rollups");
    }

    [Fact]
    public void GetActivity_NoModelOpen_IsNullModel_WithTheFooterStatus()
    {
        Session(Guid.NewGuid().ToString(), Now.AddDays(-2), 5, closed: true);
        var data = Service(HealthContext.Empty).GetActivity();
        Assert.Null(data.Model);
        Assert.Equal(1, data.Status.OutboxFileCount);
    }
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
