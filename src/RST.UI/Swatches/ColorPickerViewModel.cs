// Copied from splash-it src/SplashIt.UI/Swatches/ColorPickerViewModel.cs (splash-it commit ba86a71; originally gantt-it src/GanttIt.Revit/UI/Swatches/ColorPickerViewModel.cs, gantt-it commit a96d0a1), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*).

using System.Windows.Input;
using System.Windows.Media;
using RST.Core.Colors;
using RST.UI.Mvvm;
using RST.UI.Resources;

namespace RST.UI.Swatches;

/// <summary>One clickable color in the picker.</summary>
public sealed class SwatchViewModel(Rgb color, string automationId)
{
    public Rgb Color { get; } = color;

    public string Hex => Color.ToHex();

    public string AutomationId { get; } = automationId;

    public Brush Brush { get; } = BrushFor(color);

    public static Brush BrushFor(Rgb color)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// The color picker (gantt-it §2.4): the 12-swatch palette, a recent-colors row, a
/// <c>#RRGGBB</c> field, "Pick from screen" (eyedropper) and "More…" for the
/// Windows color dialog. Every way of choosing ends in <see cref="Pick"/>, so a
/// color picked from the screen joins the recent colors like any other.
/// </summary>
public sealed class ColorPickerViewModel : ObservableObject
{
    private readonly RecentColors _recent;
    private readonly Func<Rgb, Rgb?> _moreColors;
    private readonly Func<Rgb?>? _pickFromScreen;
    private string _hexText;
    private string? _hexError;

    /// <param name="pickFromScreen">Runs the eyedropper; null when cancelled. Null hides the button.</param>
    public ColorPickerViewModel(
        Rgb current, IReadOnlyList<Rgb> palette, RecentColors recent, Func<Rgb, Rgb?> moreColors, Func<Rgb?>? pickFromScreen = null)
    {
        Current = current;
        _recent = recent;
        _moreColors = moreColors;
        _pickFromScreen = pickFromScreen;
        _hexText = current.ToHex();
        Palette = palette.Select((c, i) => new SwatchViewModel(c, $"ColorPicker.Swatch{i + 1}")).ToList();
        Recent = recent.Items.Select((c, i) => new SwatchViewModel(c, $"ColorPicker.Recent{i + 1}")).ToList();
        PickCommand = new RelayCommand<SwatchViewModel>(s => Pick(s.Color));
        ApplyHexCommand = new RelayCommand(ApplyHex);
        MoreCommand = new RelayCommand(More);
        PickFromScreenCommand = new RelayCommand(PickFromScreen, () => CanPickFromScreen);
    }

    /// <summary>Raised once with the chosen color.</summary>
    public event Action<Rgb>? Picked;

    public Rgb Current { get; }

    public Brush CurrentBrush => SwatchViewModel.BrushFor(Current);

    public IReadOnlyList<SwatchViewModel> Palette { get; }

    public IReadOnlyList<SwatchViewModel> Recent { get; }

    public bool HasRecent => Recent.Count > 0;

    public string HexText
    {
        get => _hexText;
        set
        {
            if (SetField(ref _hexText, value)) HexError = null;
        }
    }

    public string? HexError
    {
        get => _hexError;
        private set
        {
            if (SetField(ref _hexError, value)) OnPropertyChanged(nameof(HasHexError));
        }
    }

    public bool HasHexError => HexError is not null;

    public ICommand PickCommand { get; }

    public ICommand ApplyHexCommand { get; }

    public ICommand MoreCommand { get; }

    public ICommand PickFromScreenCommand { get; }

    public bool CanPickFromScreen => _pickFromScreen is not null;

    private void ApplyHex()
    {
        if (Rgb.TryParse(HexText, out var color)) Pick(color);
        else HexError = UiStrings.Color_HexError;
    }

    private void More()
    {
        if (_moreColors(Current) is { } color) Pick(color);
    }

    private void PickFromScreen()
    {
        if (_pickFromScreen is null) return;
        HexError = null;
        Rgb? color;
        try
        {
            color = _pickFromScreen();
        }
        catch (Exception ex)
        {
            HexError = UiStrings.Color_PickFailed(ex.Message);
            return;
        }
        if (color is { } c) Pick(c);
    }

    private void Pick(Rgb color)
    {
        _recent.Add(color);
        Picked?.Invoke(color);
    }
}
