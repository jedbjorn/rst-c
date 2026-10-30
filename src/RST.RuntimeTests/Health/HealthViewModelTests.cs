// rst-c's own (not vendored): the Health window's flows against a scripted service (spec #16, WU2,
// AC5): open, scan (success, failure, busy gating), clean (nothing selected, cancel at the
// confirm, success with the per-target lines, failure), the collection toggle (success, failure
// revert), the range pills, the Activity states, and the session timer starting and stopping
// with the tab.

using System.Globalization;
using System.IO;
using RST.Core.Health;
using RST.RuntimeTests.Support;
using RST.UI.Controls;
using RST.UI.Health;
using Xunit;

namespace RST.RuntimeTests.Health;

public class HealthViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 14, 0, 0, TimeSpan.Zero);

    /// <summary>A hand-cranked repeating timer.</summary>
    private sealed class FakeTimer
    {
        public List<(TimeSpan Interval, Action Tick, Handle Handle)> Started { get; } = [];

        public IDisposable Start(TimeSpan interval, Action tick)
        {
            var handle = new Handle();
            Started.Add((interval, tick, handle));
            return handle;
        }

        public bool Running => Started.Count > 0 && !Started[^1].Handle.Stopped;

        public void Tick() => Started[^1].Tick();

        public sealed class Handle : IDisposable
        {
            public bool Stopped { get; private set; }

            public void Dispose() => Stopped = true;
        }
    }

    private sealed class Harness
    {
        public FakeHealthService Service { get; } = new();

        public FakeDialogs Dialogs { get; } = new();

        public FakeTimer Timer { get; } = new();

        public DateTimeOffset Clock { get; set; } = Now;

        public Func<Action, Task> Runner { get; set; } = work =>
        {
            work();
            return Task.CompletedTask;
        };

        public HealthViewModel Build() => new(Service, Dialogs, new HealthViewModelOptions
        {
            RunInBackground = a => Runner(a),
            StartTimer = Timer.Start,
            Now = () => Clock,
            Culture = CultureInfo.GetCultureInfo("en-US"),
            Zone = TimeZoneInfo.Utc,
            ToastSchedule = (_, _) => new NoTimer(),
        });

        private sealed class NoTimer : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private static void Sta(Action action) => Screenshots.RunSta(() =>
    {
        // No dispatcher context: awaited continuations run inline, so a test can drive a pending scan to completion.
        SynchronizationContext.SetSynchronizationContext(null);
        action();
    });

    private static HealthRow Row(HealthViewModel vm, string section, string label) =>
        vm.Sections.Single(s => s.Title == section).Rows.First(r => r.Label == label);

    [Fact]
    public void Open_WithoutASnapshot_RendersEveryValueAsADash() => Sta(() =>
    {
        var h = new Harness();
        var vm = h.Build();
        vm.Load();
        Assert.Equal("—", vm.Device);
        Assert.Equal("—", vm.Captured);
        Assert.Equal(["Hardware", "Display & Network", "Revit", "Operating System"], vm.Sections.Select(s => s.Title));
        Assert.All(vm.Sections, s => Assert.False(s.IsExpanded));
        foreach (var row in vm.Sections.SelectMany(s => s.Rows).Where(r => r.Label != "Hardware Accel"))
            Assert.True(row.IsMissing && row.Value == "—", row.ToString());
        Assert.Equal("Unknown", Row(vm, "Revit", "Hardware Accel").Value);
    });

    [Fact]
    public void Open_WithASnapshot_ShowsItsRows() => Sta(() =>
    {
        var h = new Harness();
        h.Service.Snapshot = HealthSamples.Snapshot();
        var vm = h.Build();
        vm.Load();
        Assert.Equal("WS-ARCH-042", vm.Device);
        Assert.Equal("10/30/2026 2:05:12 PM", vm.Captured.Replace(' ', ' '));
        Assert.Equal("32,768 MB", Row(vm, "Hardware", "RAM").Value);
    });

    [Fact]
    public void Scan_Success_RendersTheNewSnapshot() => Sta(() =>
    {
        var h = new Harness();
        h.Service.ScanResult = HealthSamples.Snapshot();
        var vm = h.Build();
        vm.Load();
        vm.ScanAsync().GetAwaiter().GetResult();
        Assert.Equal(1, h.Service.ScanCalls);
        Assert.Equal("WS-ARCH-042", vm.Device);
        Assert.False(vm.IsScanning);
        Assert.False(vm.Toast.IsOpen);
    });

    [Fact]
    public void Scan_Failure_ToastsScanFailed_AndKeepsTheOldValues() => Sta(() =>
    {
        var h = new Harness();
        h.Service.Snapshot = HealthSamples.Healthy();
        h.Service.ScanError = new InvalidOperationException("WMI is not available");
        var vm = h.Build();
        vm.Load();
        vm.ScanAsync().GetAwaiter().GetResult();
        Assert.True(vm.Toast.IsOpen);
        Assert.Equal("Scan failed", vm.Toast.Title);
        Assert.Equal("WMI is not available", vm.Toast.Text);
        Assert.Equal(BannerKind.Error, vm.Toast.Kind);
        Assert.Equal("WS-ARCH-042", vm.Device);
        Assert.False(vm.IsScanning);
    });

    [Fact]
    public void Scan_RunsInTheBackground_AndDisablesEveryCommandUntilDone() => Sta(() =>
    {
        var h = new Harness();
        var gate = new TaskCompletionSource();
        Action? pending = null;
        h.Runner = work =>
        {
            pending = work;
            return gate.Task;
        };
        var vm = h.Build();
        var scan = vm.ScanAsync();

        Assert.True(vm.IsScanning);
        Assert.Equal("Scanning…", vm.ScanLabel);
        Assert.True(vm.IsBusy);
        Assert.False(vm.IsIdle);
        Assert.False(vm.ScanCommand.CanExecute(null));
        Assert.False(vm.CleanCommand.CanExecute(null));
        Assert.False(vm.CloseCommand.CanExecute(null));
        Assert.False(vm.JunkRunCommand.CanExecute(null));
        // A second scan while one runs does nothing.
        vm.ScanAsync().GetAwaiter().GetResult();
        vm.OpenJunk();
        Assert.False(vm.IsJunkOpen);

        pending!();
        gate.SetResult();
        scan.GetAwaiter().GetResult();
        Assert.Equal(1, h.Service.ScanCalls);
        Assert.False(vm.IsScanning);
        Assert.Equal("Scan System", vm.ScanLabel);
        Assert.True(vm.ScanCommand.CanExecute(null));
        Assert.True(vm.CloseCommand.CanExecute(null));
    });

    [Fact]
    public void Clean_OpensTheModal_WithEveryTargetOn() => Sta(() =>
    {
        var h = new Harness();
        h.Service.Targets = HealthSamples.Targets();
        var vm = h.Build();
        vm.CleanCommand.Execute(null);
        Assert.True(vm.IsJunkOpen);
        Assert.False(vm.IsMainEnabled);
        Assert.Equal(["Revit journals", "Collaboration cache", "Recent files list"], vm.JunkTargets.Select(t => t.DisplayName));
        Assert.All(vm.JunkTargets, t => Assert.True(t.IsSelected));
        Assert.True(vm.HasJunkTargets);
    });

    [Fact]
    public void Clean_NoTargets_ShowsTheEmptyState() => Sta(() =>
    {
        var h = new Harness();
        var vm = h.Build();
        vm.OpenJunk();
        Assert.True(vm.IsJunkOpen);
        Assert.False(vm.HasJunkTargets);
    });

    [Fact]
    public void Clean_Cancel_ClosesTheModal_WithoutCleaning() => Sta(() =>
    {
        var h = new Harness();
        h.Service.Targets = HealthSamples.Targets();
        var vm = h.Build();
        vm.OpenJunk();
        vm.JunkCancelCommand.Execute(null);
        Assert.False(vm.IsJunkOpen);
        Assert.True(vm.IsMainEnabled);
        Assert.Empty(h.Service.CleanCalls);
        Assert.Empty(h.Dialogs.ConfirmCalls);
    });

    [Fact]
    public void Clean_NothingSelected_ToastsNothingSelected_AndAsksNothing() => Sta(() =>
    {
        var h = new Harness();
        h.Service.Targets = HealthSamples.Targets();
        var vm = h.Build();
        vm.OpenJunk();
        foreach (var t in vm.JunkTargets) t.IsSelected = false;
        vm.RunJunkAsync().GetAwaiter().GetResult();
        Assert.False(vm.IsJunkOpen);
        Assert.Empty(h.Dialogs.ConfirmCalls);
        Assert.Empty(h.Service.CleanCalls);
        Assert.Equal("Nothing selected", vm.Toast.Title);
        Assert.Equal("No targets were chosen for cleanup.", vm.Toast.Text);
    });

    [Fact]
    public void Clean_CancelAtTheConfirm_CleansNothing() => Sta(() =>
    {
        var h = new Harness();
        h.Service.Targets = HealthSamples.Targets();
        h.Dialogs.ConfirmResult = false;
        var vm = h.Build();
        vm.OpenJunk();
        vm.RunJunkAsync().GetAwaiter().GetResult();
        var confirm = Assert.Single(h.Dialogs.ConfirmCalls);
        Assert.Equal("Delete selected junk?", confirm.Title);
        Assert.Equal("This will permanently delete files in the selected targets. Files locked by running applications are skipped.", confirm.Message);
        Assert.Equal("Continue", confirm.ConfirmText);
        Assert.True(confirm.Danger);
        Assert.Empty(h.Service.CleanCalls);
        Assert.False(vm.Busy.IsBusy);
        Assert.False(vm.Toast.IsOpen);
    });

    [Fact]
    public void Clean_Success_CleansTheSelectedIds_UnderTheCleaningOverlay_AndReportsEachTarget() => Sta(() =>
    {
        var h = new Harness();
        h.Service.Targets = HealthSamples.Targets();
        h.Service.CleanResult = HealthSamples.CleanResult();
        h.Dialogs.ConfirmResult = true;
        var vm = h.Build();
        var busyDuringClean = false;
        string? busyTitle = null, busyMessage = null;
        h.Runner = work =>
        {
            busyDuringClean = vm.Busy.IsBusy && !vm.IsIdle && !vm.IsMainEnabled;
            busyTitle = vm.Busy.Title;
            busyMessage = vm.Busy.Message;
            work();
            return Task.CompletedTask;
        };
        vm.OpenJunk();
        vm.JunkTargets[2].IsSelected = false;
        vm.RunJunkAsync().GetAwaiter().GetResult();

        Assert.Equal(["revit-journals", "collab-cache"], Assert.Single(h.Service.CleanCalls));
        Assert.True(busyDuringClean);
        Assert.Equal("Cleaning", busyTitle);
        Assert.Equal("Deleting selected junk files", busyMessage);
        Assert.False(vm.Busy.IsBusy);
        Assert.Equal("Cleanup complete", vm.Toast.Title);
        Assert.Equal("Revit journals: 1,284 deleted\nCollaboration cache: 9,310 deleted, 2 locked/in-use", vm.Toast.Text);
        Assert.Equal(BannerKind.Success, vm.Toast.Kind);
    });

    [Fact]
    public void Clean_Failure_HidesTheOverlay_AndToastsCleanupFailed() => Sta(() =>
    {
        var h = new Harness();
        h.Service.Targets = HealthSamples.Targets();
        h.Service.CleanError = new IOException("The disk is full");
        h.Dialogs.ConfirmResult = true;
        var vm = h.Build();
        vm.OpenJunk();
        vm.RunJunkAsync().GetAwaiter().GetResult();
        Assert.False(vm.Busy.IsBusy);
        Assert.Equal("Cleanup failed", vm.Toast.Title);
        Assert.Equal("The disk is full", vm.Toast.Text);
    });

    [Fact]
    public void ActivityTab_LoadsTheDefaultRange_AndStartsTheSessionTimer() => Sta(() =>
    {
        var h = new Harness();
        h.Service.Activity = days => HealthSamples.Activity(Now, days);
        var vm = h.Build();
        Assert.False(vm.IsTimerRunning);

        vm.SelectedTab = HealthViewModel.ActivityTab;
        Assert.Equal([7], h.Service.ActivityCalls);
        Assert.True(h.Timer.Running);
        Assert.True(vm.IsTimerRunning);
        Assert.Equal(TimeSpan.FromSeconds(1), h.Timer.Started[^1].Interval);
        Assert.Equal("02:14:09", vm.SessionTimer.Value);
        Assert.Equal("file open 01:03:41", vm.SessionTimer.Sub);

        h.Clock = Now.AddSeconds(5);
        h.Timer.Tick();
        Assert.Equal("02:14:14", vm.SessionTimer.Value);

        Assert.Equal(["Session Timer", "Session Total", "Longest Sync", "Longest Open Time", "Warning Count", "File Size"],
            vm.StatCards.Select(c => c.Label));
        Assert.Equal("26h", vm.SessionTotal.Value);
        Assert.Equal("64s", vm.LongestSync.Value);
        Assert.Equal("1m 35s", vm.LongestOpen.Value);
        Assert.Equal("612", vm.WarningCount.Value);
        Assert.Equal("1.7 GB", vm.FileSize.Value);
        Assert.Equal("collection on · 23 files · 1.4 MB · since 9/19/2026", vm.TelemetryStatusLine);
        Assert.True(vm.TelemetryEnabled);
        Assert.All(vm.Charts, c => Assert.Equal(ChartMode.Curve, c.Chart!.Mode));
    });

    [Fact]
    public void LeavingTheActivityTab_StopsTheTimer() => Sta(() =>
    {
        var h = new Harness();
        h.Service.Activity = days => HealthSamples.Activity(Now, days);
        var vm = h.Build();
        vm.SelectedTab = HealthViewModel.ActivityTab;
        Assert.True(h.Timer.Running);
        vm.SelectedTab = HealthViewModel.HealthTab;
        Assert.False(h.Timer.Running);
        Assert.False(vm.IsTimerRunning);

        // Coming back starts a fresh one; closing the window stops it.
        vm.SelectedTab = HealthViewModel.ActivityTab;
        Assert.True(h.Timer.Running);
        Assert.Equal(2, h.Timer.Started.Count);
        vm.Dispose();
        Assert.False(h.Timer.Running);
    });

    [Fact]
    public void RangeSwitch_ReloadsWithTheNewRange_AndSelectsOnlyThatPill() => Sta(() =>
    {
        var h = new Harness();
        h.Service.Activity = days => HealthSamples.Activity(Now, days);
        var vm = h.Build();
        Assert.Equal(["7D", "1M", "3M", "6M"], vm.Ranges.Select(r => r.Label));
        Assert.True(vm.Ranges[0].IsSelected);
        vm.SelectedTab = HealthViewModel.ActivityTab;

        vm.Ranges[2].IsSelected = true; // the user checks 3M
        Assert.Equal([7, 90], h.Service.ActivityCalls);
        Assert.Equal(90, vm.RangeDays);
        Assert.Equal([false, false, true, false], vm.Ranges.Select(r => r.IsSelected));

        vm.SelectRange(30);
        Assert.Equal([7, 90, 30], h.Service.ActivityCalls);
        Assert.Equal([false, true, false, false], vm.Ranges.Select(r => r.IsSelected));
    });

    [Fact]
    public void Activity_SparseStates_SingleDot_NoTime_NotWorkshared_NoTimer() => Sta(() =>
    {
        var h = new Harness();
        h.Service.Activity = _ => HealthSamples.SparseActivity(Now);
        var vm = h.Build();
        vm.SelectedTab = HealthViewModel.ActivityTab;

        Assert.Null(vm.SessionChart.Chart);
        Assert.Equal("No time recorded in this range", vm.SessionChart.EmptyTitle);
        Assert.Equal("This model has no usable identity key yet — save it first.", vm.SessionChart.EmptyHint);

        Assert.Equal(ChartMode.Dots, vm.OpenChart.Chart!.Mode);
        Assert.Null(vm.OpenChart.Chart.Curve);
        Assert.Equal("", vm.OpenChart.EmptyTitle);
        Assert.Equal("not enough history yet", vm.OpenChart.EmptyHint);

        Assert.Null(vm.SyncChart.Chart);
        Assert.Equal("Not a workshared model", vm.SyncChart.EmptyTitle);
        Assert.Equal("Sync history appears for workshared and cloud models.", vm.SyncChart.EmptyHint);

        Assert.Equal("—", vm.SessionTimer.Value);
        Assert.Equal("", vm.SessionTimer.Sub);
        Assert.False(h.Timer.Running);
        Assert.Equal("collection off · 0 files · 0 B", vm.TelemetryStatusLine);
        Assert.False(vm.TelemetryEnabled);
        Assert.Equal("0h", vm.SessionTotal.Value);
        Assert.Equal("—", vm.LongestSync.Value);
        Assert.Equal("6.4s", vm.LongestOpen.Value);
        Assert.Equal("18 MB", vm.FileSize.Value);
    });

    [Fact]
    public void Activity_NoModelOpen_AndNoOpensOrSyncs() => Sta(() =>
    {
        var h = new Harness();
        h.Service.Activity = _ => new ActivityData { Status = new TelemetryStatus(true, 1, 2048, null) };
        var vm = h.Build();
        vm.SelectedTab = HealthViewModel.ActivityTab;
        Assert.All(vm.Charts, c =>
        {
            Assert.Null(c.Chart);
            Assert.Equal("No model open", c.EmptyTitle);
            Assert.Equal("Open a model to see its activity history.", c.EmptyHint);
        });
        Assert.All(vm.StatCards.Skip(1), c => Assert.Equal("—", c.Value));

        h.Service.Activity = _ => new ActivityData
        {
            File = new ActivityFile { MatchedKeys = "central", IsWorkshared = true },
            Status = new TelemetryStatus(true, 1, 2048, null),
        };
        vm.LoadActivity();
        Assert.Equal("No time recorded in this range", vm.SessionChart.EmptyTitle);
        Assert.Equal("", vm.SessionChart.EmptyHint);
        Assert.Equal("No opens in this range", vm.OpenChart.EmptyTitle);
        Assert.Equal("No syncs in this range", vm.SyncChart.EmptyTitle);
    });

    [Fact]
    public void Activity_Failure_ShowsCouldNotLoad_AndStopsTheTimer() => Sta(() =>
    {
        var h = new Harness();
        h.Service.Activity = days => HealthSamples.Activity(Now, days);
        var vm = h.Build();
        vm.SelectedTab = HealthViewModel.ActivityTab;
        Assert.True(h.Timer.Running);

        h.Service.ActivityError = new IOException("outbox locked");
        vm.SelectRange(30);
        Assert.False(h.Timer.Running);
        Assert.All(vm.Charts, c =>
        {
            Assert.Equal("Could not load activity", c.EmptyTitle);
            Assert.Equal("outbox locked", c.EmptyHint);
        });
        Assert.All(vm.StatCards.Skip(1), c => Assert.Equal("—", c.Value));
    });

    [Fact]
    public void TelemetryToggle_Success_SavesAndReloads() => Sta(() =>
    {
        var h = new Harness();
        h.Service.Activity = days => HealthSamples.Activity(Now, days);
        var vm = h.Build();
        vm.SelectedTab = HealthViewModel.ActivityTab;
        Assert.True(vm.TelemetryEnabled);

        h.Service.Activity = _ => HealthSamples.SparseActivity(Now); // collection now off on disk
        vm.TelemetryEnabled = false;
        Assert.Equal([false], h.Service.ToggleCalls);
        Assert.Equal(2, h.Service.ActivityCalls.Count);
        Assert.False(vm.TelemetryEnabled);
        Assert.StartsWith("collection off", vm.TelemetryStatusLine, StringComparison.Ordinal);
        Assert.False(vm.Toast.IsOpen);
    });

    [Fact]
    public void TelemetryToggle_Failure_RevertsAndToasts() => Sta(() =>
    {
        var h = new Harness();
        h.Service.Activity = days => HealthSamples.Activity(Now, days);
        var vm = h.Build();
        vm.SelectedTab = HealthViewModel.ActivityTab;

        h.Service.ToggleResult = new TelemetryToggleResult(false, true, "failed to save preference");
        vm.TelemetryEnabled = false;
        Assert.True(vm.TelemetryEnabled);
        Assert.Equal("Could not change collection", vm.Toast.Title);
        Assert.Equal("failed to save preference", vm.Toast.Text);
        Assert.Single(h.Service.ActivityCalls);

        // A throw reverts to the state before the click.
        h.Service.ToggleError = new UnauthorizedAccessException("access denied");
        vm.TelemetryEnabled = false;
        Assert.True(vm.TelemetryEnabled);
        Assert.Equal("access denied", vm.Toast.Text);

        // A failure without a reported state reverts the click.
        h.Service.ToggleError = null;
        h.Service.ToggleResult = new TelemetryToggleResult(false, null, null);
        vm.TelemetryEnabled = false;
        Assert.True(vm.TelemetryEnabled);
        Assert.Equal("Unknown error", vm.Toast.Text);
    });

    [Fact]
    public void Close_RaisesRequestClose() => Sta(() =>
    {
        var h = new Harness();
        var vm = h.Build();
        var closed = 0;
        vm.RequestClose += (_, _) => closed++;
        vm.CloseCommand.Execute(null);
        Assert.Equal(1, closed);
    });

    /// <summary>A readable but malformed health_scan.json: null entries a scan never writes.</summary>
    private static HealthSnapshot Malformed()
    {
        var s = HealthSamples.Snapshot();
        s.Gpus = [null!];
        s.Cleanup!.Targets = [null!];
        return s;
    }

    [Fact]
    public void Open_MalformedSnapshot_FallsBackToEveryValueADash() => Sta(() =>
    {
        // Review N1 (spec #16, Beyond Intention): the window still opens, as the HTML's render(null).
        var h = new Harness();
        h.Service.Snapshot = Malformed();
        var vm = h.Build();
        vm.Load();
        Assert.Equal("—", vm.Device);
        Assert.Equal(4, vm.Sections.Count);
        Assert.True(Row(vm, "Hardware", "RAM").IsMissing);
        Assert.False(vm.Toast.IsOpen);
    });

    [Fact]
    public void Scan_MalformedResult_FallsBackToEveryValueADash() => Sta(() =>
    {
        var h = new Harness();
        h.Service.Snapshot = HealthSamples.Snapshot();
        h.Service.ScanResult = Malformed();
        var vm = h.Build();
        vm.Load();
        vm.ScanAsync().GetAwaiter().GetResult();
        Assert.Equal("—", vm.Device);
        Assert.False(vm.IsScanning);
    });

    [Fact]
    public void WorkLeftRunningByAClosedWindow_DisablesScanAndClean_UntilItEnds() => Sta(() =>
    {
        // Review N2: a window reopened while the last one's scan or clean still runs cannot start another.
        var h = new Harness();
        h.Service.Targets = HealthSamples.Targets();
        h.Service.IsWorkRunning = true;
        var vm = h.Build();

        Assert.False(vm.CanStartWork);
        Assert.False(vm.ScanCommand.CanExecute(null));
        Assert.False(vm.CleanCommand.CanExecute(null));
        Assert.False(vm.JunkRunCommand.CanExecute(null));
        Assert.True(vm.CloseCommand.CanExecute(null));
        Assert.True(vm.IsIdle);
        vm.ScanAsync().GetAwaiter().GetResult();
        vm.OpenJunk();
        Assert.Equal(0, h.Service.ScanCalls);
        Assert.False(vm.IsJunkOpen);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        h.Service.FinishWork();
        Assert.Contains(nameof(HealthViewModel.CanStartWork), changed);
        Assert.True(vm.CanStartWork);
        Assert.True(vm.ScanCommand.CanExecute(null));
        vm.ScanAsync().GetAwaiter().GetResult();
        Assert.Equal(1, h.Service.ScanCalls);

        // Closing the window unsubscribes.
        vm.Dispose();
        changed.Clear();
        h.Service.FinishWork();
        Assert.Empty(changed);
    });
}
