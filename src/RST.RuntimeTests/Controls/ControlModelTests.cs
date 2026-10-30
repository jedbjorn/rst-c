// rst-c's own (not vendored): TransientMessage, BusyState and WindowPlacement (spec #16, WU1).

using System.Windows;
using System.Windows.Interop;
using RST.RuntimeTests.Support;
using RST.UI.Controls;
using RST.UI.Dialogs;
using Xunit;

namespace RST.RuntimeTests.Controls;

public class TransientMessageTests
{
    /// <summary>A hand-cranked clock: records each scheduled action so the test fires or cancels it.</summary>
    private sealed class FakeClock
    {
        public List<(TimeSpan Delay, Action Action, Handle Handle)> Scheduled { get; } = [];

        public IDisposable Schedule(TimeSpan delay, Action action)
        {
            var handle = new Handle();
            Scheduled.Add((delay, action, handle));
            return handle;
        }

        public void FireLast()
        {
            var (_, action, handle) = Scheduled[^1];
            if (!handle.Cancelled) action();
        }

        public sealed class Handle : IDisposable
        {
            public bool Cancelled { get; private set; }

            public void Dispose() => Cancelled = true;
        }
    }

    [Fact]
    public void Show_Opens_AndHidesAfterTheInformDuration()
    {
        var clock = new FakeClock();
        var toast = new TransientMessage(clock.Schedule);
        toast.Show("Saved", BannerKind.Success, title: "Profile");

        Assert.True(toast.IsOpen);
        Assert.Equal("Saved", toast.Text);
        Assert.Equal("Profile", toast.Title);
        Assert.True(toast.HasTitle);
        Assert.Equal(BannerKind.Success, toast.Kind);
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(clock.Scheduled).Delay);

        clock.FireLast();
        Assert.False(toast.IsOpen);
    }

    [Fact]
    public void TheDurations_AreTheHtmlOnes()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(3000), TransientMessage.InformDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(2400), TransientMessage.ToastDuration);
    }

    [Fact]
    public void ASecondShow_ReplacesTheMessage_AndRestartsTheTimer()
    {
        var clock = new FakeClock();
        var toast = new TransientMessage(clock.Schedule);
        toast.Show("first", BannerKind.Info, TransientMessage.ToastDuration);
        toast.Show("second", BannerKind.Error, TransientMessage.ToastDuration);

        Assert.Equal(2, clock.Scheduled.Count);
        Assert.True(clock.Scheduled[0].Handle.Cancelled);
        Assert.Equal("second", toast.Text);
        Assert.Null(toast.Title);
        Assert.False(toast.HasTitle);

        Assert.True(toast.IsOpen);
        clock.FireLast();
        Assert.False(toast.IsOpen);
    }

    [Fact]
    public void AnInfiniteDuration_SchedulesNothing_UntilDismiss()
    {
        var clock = new FakeClock();
        var toast = new TransientMessage(clock.Schedule);
        toast.Show("stays", duration: Timeout.InfiniteTimeSpan);
        Assert.Empty(clock.Scheduled);
        Assert.True(toast.IsOpen);
        toast.Dismiss();
        Assert.False(toast.IsOpen);
    }

    [Fact]
    public void TheDefaultTimer_HidesTheBanner_OnTheDispatcher() => Screenshots.RunSta(() =>
    {
        var toast = new TransientMessage();
        toast.Show("auto", duration: TimeSpan.FromMilliseconds(50));
        Assert.True(toast.IsOpen);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (toast.IsOpen && DateTime.UtcNow < deadline)
        {
            WindowCapture.DoEvents();
            Thread.Sleep(10);
        }
        Assert.False(toast.IsOpen);
    });
}

public class BusyStateTests
{
    [Fact]
    public void ShowAndHide()
    {
        var busy = new BusyState();
        Assert.False(busy.IsBusy);
        busy.Show("Loading Architecture", "Applying…");
        Assert.True(busy.IsBusy);
        Assert.Equal("Loading Architecture", busy.Title);
        Assert.True(busy.HasMessage);
        busy.Show("Cleaning");
        Assert.False(busy.HasMessage);
        busy.Hide();
        Assert.False(busy.IsBusy);
    }
}

public class WindowPlacementTests
{
    [Fact]
    public void NoOwner_CentresOnTheScreen() => Screenshots.RunSta(() =>
    {
        var window = new Window();
        WindowPlacement.Own(window, IntPtr.Zero);
        Assert.Equal(WindowStartupLocation.CenterScreen, window.WindowStartupLocation);
        Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Owner);
    });

    [Fact]
    public void AnOwner_IsSet_AndTheWindowCentresOnIt() => Screenshots.RunSta(() =>
    {
        var window = new Window();
        var owner = new IntPtr(0x1234);
        WindowPlacement.Own(window, owner);
        Assert.Equal(WindowStartupLocation.CenterOwner, window.WindowStartupLocation);
        Assert.Equal(owner, new WindowInteropHelper(window).Owner);
    });

    [Fact]
    public void CapToWorkArea_CapsBothSides() => Screenshots.RunSta(() =>
    {
        var window = new Window { Width = 1450, Height = 900 };
        WindowPlacement.CapToWorkArea(window, new Rect(0, 0, 1024, 728));
        Assert.Equal(1024, window.MaxWidth);
        Assert.Equal(728, window.MaxHeight);
    });
}
