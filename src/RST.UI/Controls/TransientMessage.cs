// rst-c's own (not vendored): the model behind Theme.xaml's TransientBanner style.

using System.Windows.Threading;
using RST.UI.Mvvm;

namespace RST.UI.Controls;

/// <summary>The four looks of a banner, matching Theme.xaml's Info/Success/Warning/Error banners.</summary>
public enum BannerKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>
/// An in-window toast that hides itself (spec #16, Fidelity Rules: <c>RSTmsgInform</c> and the
/// Builder's <c>showToast</c> become a transient banner). A view-model owns one and calls
/// <see cref="Show"/>; the view shows it with
/// <c>&lt;ContentControl Style="{StaticResource TransientBanner}" Content="{Binding Toast}" /&gt;</c>.
/// A new <see cref="Show"/> replaces the current message and restarts the timer, as the HTML did.
/// </summary>
public sealed class TransientMessage : ObservableObject
{
    /// <summary><c>RSTmsgInform</c>'s auto-dismiss time (rst_messages.js: 3000 ms).</summary>
    public static readonly TimeSpan InformDuration = TimeSpan.FromSeconds(3);

    /// <summary>The Builder's <c>showToast</c> time (profile_builder.html: 2400 ms).</summary>
    public static readonly TimeSpan ToastDuration = TimeSpan.FromMilliseconds(2400);

    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private IDisposable? _pending;
    private string? _title;
    private string _text = "";
    private BannerKind _kind;
    private bool _isOpen;

    /// <param name="schedule">
    /// Runs the action once after the delay and returns a handle that cancels it. Null uses a
    /// <see cref="DispatcherTimer"/> on the calling thread's dispatcher; tests pass a fake clock.
    /// </param>
    public TransientMessage(Func<TimeSpan, Action, IDisposable>? schedule = null) =>
        _schedule = schedule ?? DispatcherSchedule;

    /// <summary>Optional bold first line (<c>RSTmsgInform</c>'s title).</summary>
    public string? Title
    {
        get => _title;
        private set
        {
            if (SetField(ref _title, value)) OnPropertyChanged(nameof(HasTitle));
        }
    }

    public bool HasTitle => !string.IsNullOrEmpty(Title);

    public string Text
    {
        get => _text;
        private set => SetField(ref _text, value);
    }

    public BannerKind Kind
    {
        get => _kind;
        private set => SetField(ref _kind, value);
    }

    /// <summary>True while the banner is shown.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        private set => SetField(ref _isOpen, value);
    }

    /// <summary>
    /// Shows <paramref name="text"/> for <paramref name="duration"/> (default <see cref="InformDuration"/>;
    /// <see cref="Timeout.InfiniteTimeSpan"/> or zero keeps it until <see cref="Dismiss"/>).
    /// </summary>
    public void Show(string text, BannerKind kind = BannerKind.Info, TimeSpan? duration = null, string? title = null)
    {
        _pending?.Dispose();
        _pending = null;
        Title = title;
        Text = text;
        Kind = kind;
        IsOpen = true;
        var d = duration ?? InformDuration;
        if (d > TimeSpan.Zero) _pending = _schedule(d, Dismiss);
    }

    /// <summary>Hides the banner now and cancels its timer.</summary>
    public void Dismiss()
    {
        _pending?.Dispose();
        _pending = null;
        IsOpen = false;
    }

    private static IDisposable DispatcherSchedule(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
        return new Cancel(timer.Stop);
    }

    private sealed class Cancel(Action cancel) : IDisposable
    {
        public void Dispose() => cancel();
    }
}
