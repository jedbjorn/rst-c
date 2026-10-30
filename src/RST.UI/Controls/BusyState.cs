// rst-c's own (not vendored): the model behind Theme.xaml's BusyOverlay style.

using RST.UI.Mvvm;

namespace RST.UI.Controls;

/// <summary>
/// A window's modal busy overlay (spec #16, Fidelity Rules: <c>RSTmsgWait</c> becomes a busy
/// overlay inside the window, with the same text). A view-model owns one, calls
/// <see cref="Show"/> before long work and <see cref="Hide"/> after; the view shows it with
/// <c>&lt;ContentControl Style="{StaticResource BusyOverlay}" Content="{Binding Busy}" /&gt;</c>
/// as the last child of the window's root grid. The overlay blocks the pointer; the view-model
/// still disables its commands while <see cref="IsBusy"/> (the keyboard is not blocked).
/// </summary>
public sealed class BusyState : ObservableObject
{
    private bool _isBusy;
    private string _title = "";
    private string? _message;

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    /// <summary><c>RSTmsgWait</c>'s title, e.g. "Loading Architecture".</summary>
    public string Title
    {
        get => _title;
        private set => SetField(ref _title, value);
    }

    /// <summary><c>RSTmsgWait</c>'s body; null or empty hides the line.</summary>
    public string? Message
    {
        get => _message;
        private set
        {
            if (SetField(ref _message, value)) OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public void Show(string title, string? message = null)
    {
        Title = title;
        Message = message;
        IsBusy = true;
    }

    public void Hide() => IsBusy = false;
}
