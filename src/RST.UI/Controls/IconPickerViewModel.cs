// rst-c's own (not vendored): the icon picker's view-model (spec #16, WU5, Shared controls): the
// rows of RST.Core's IconPickerModel as tile and variant view-models, the pack images, and the
// outcome callback. The variant images come from the shipped pack; a variant whose image is
// missing is disabled and logged once (the page's img.onerror), so it can never apply.

using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using RST.Core.Builder;
using RST.Core.Ribbon;
using RST.UI.Mvvm;
using RST.UI.Resources;
using RST.Core.Logging;

namespace RST.UI.Controls;

/// <summary>A design tile (or the None tile).</summary>
public sealed class IconTileViewModel(string? design, ImageSource? thumbnail, bool selected, bool open, ICommand click)
{
    public string? Design { get; } = design;

    public bool IsNone => Design is null;

    /// <summary>"none", or the design's name.</summary>
    public string Label => Design ?? UiStrings.Builder_IconNone;

    public string ToolTip => Design ?? UiStrings.Builder_IconNoneTip;

    /// <summary>The blue representative thumbnail.</summary>
    public ImageSource? Thumbnail { get; } = thumbnail;

    public bool IsSelected { get; } = selected;

    /// <summary>The tile whose variant row is open (<c>.popen</c>).</summary>
    public bool IsOpen { get; } = open;

    public ICommand ClickCommand { get; } = click;
}

/// <summary>One color of the open design.</summary>
public sealed class IconVariantViewModel(string design, string color, ImageSource? image, bool selected, bool enabled, ICommand click)
{
    public string Design { get; } = design;

    public string Color { get; } = color;

    public ImageSource? Image { get; } = image;

    public bool IsSelected { get; } = selected;

    public bool IsEnabled { get; } = enabled;

    /// <summary>"move — light grey" (readableKey of both).</summary>
    public string ToolTip => UiStrings.Builder_IconVariantTip(Design.Replace('_', ' '), Color.Replace('_', ' '));

    public ICommand ClickCommand { get; } = click;
}

/// <summary>A laid-out row: tiles, or the variant row (full width, under its tile's row).</summary>
public sealed class IconPickerRowViewModel(IReadOnlyList<IconTileViewModel> tiles, IReadOnlyList<IconVariantViewModel> variants)
{
    public IReadOnlyList<IconTileViewModel> Tiles { get; } = tiles;

    public IReadOnlyList<IconVariantViewModel> Variants { get; } = variants;

    public bool IsVariantRow => Variants.Count > 0;
}

public sealed class IconPickerViewModel : ObservableObject
{
    /// <summary>Tiles per row: the page's 680 px card fit seven 86 px tiles (the WPF card is 700 px to fit the scroll bar too).</summary>
    public const int Columns = 7;

    private readonly string? _packDirectory;
    private readonly Action<IconPickerOutcome> _closed;
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);

    /// <param name="entries">The icon pack catalogue.</param>
    /// <param name="currentIconFile">The slot's <c>iconFile</c>.</param>
    /// <param name="packDirectory">The pack folder; null is the shipped one.</param>
    /// <param name="closed">Called once with the outcome (a committed value, or cancelled).</param>
    public IconPickerViewModel(IReadOnlyList<IconPackEntry>? entries, string? currentIconFile, string? packDirectory, Action<IconPickerOutcome> closed)
    {
        Model = new IconPickerModel(entries, currentIconFile);
        _packDirectory = packDirectory;
        _closed = closed ?? throw new ArgumentNullException(nameof(closed));
        TileCommand = new RelayCommand<string>(d => Act(() => Model.ClickTile(d)));
        NoneCommand = new RelayCommand(() => Act(Model.ClickNone));
        VariantCommand = new RelayCommand<string>(c => Act(() => Model.ClickVariant(c)));
        CancelCommand = new RelayCommand(() => Act(Model.Cancel));
        Render();
    }

    public IconPickerModel Model { get; }

    public ObservableCollection<IconPickerRowViewModel> Rows { get; } = [];

    public ICommand TileCommand { get; }

    public ICommand NoneCommand { get; }

    public ICommand VariantCommand { get; }

    public ICommand CancelCommand { get; }

    /// <summary>Raised after each change, so the view can move keyboard focus (the model's FocusedColor / FocusedTile).</summary>
    public event Action? Changed;

    /// <summary>Escape: the variant row first, then the picker.</summary>
    public void Escape() => Act(Model.Escape);

    /// <summary>A click on the backdrop, outside the card.</summary>
    public void OutsideClick() => Act(Model.Cancel);

    /// <summary>The variant row's keys.</summary>
    public void Key(PickerKey key) => Act(() => Model.Key(key));

    /// <summary>Enter or Space on the focused variant.</summary>
    public void ActivateFocused()
    {
        if (Model.FocusedColor is { } color) Act(() => Model.ClickVariant(color));
    }

    private void Act(Action action)
    {
        if (!Model.IsOpen) return;
        action();
        if (Model.Outcome is { } outcome)
        {
            _closed(outcome);
            return;
        }
        Render();
        Changed?.Invoke();
    }

    private void Render()
    {
        // Variant images first: a missing one disables the variant before the row is drawn.
        var images = new Dictionary<string, ImageSource?>(StringComparer.Ordinal);
        if (Model.OpenDesign is { } open)
        {
            foreach (var color in Model.OpenColors)
            {
                var image = PackIcons.Load(new IconPackValue(open, color), _packDirectory);
                images[color] = image;
                if (image is null)
                {
                    Model.MarkBroken(open, color);
                    if (_logged.Add(open + "|" + color)) Log.Error("iconpack variant image failed: {Name} {Color}", open, color);
                }
            }
        }

        Rows.Clear();
        foreach (var row in Model.Rows(Columns))
        {
            if (row.IsVariantRow)
            {
                var design = Model.OpenDesign!;
                Rows.Add(new IconPickerRowViewModel([], row.Colors.Select(c => new IconVariantViewModel(
                    design, c, images.GetValueOrDefault(c),
                    design == Model.SelectedDesign && c == Model.SelectedColor,
                    Model.IsVariantEnabled(design, c),
                    VariantCommand)).ToList()));
                continue;
            }
            Rows.Add(new IconPickerRowViewModel(row.Tiles.Select(t => t is null
                ? new IconTileViewModel(null, null, Model.NoneSelected, false, NoneCommand)
                : new IconTileViewModel(t.Name, PackIcons.Load(new IconPackValue(t.Name, "blue"), _packDirectory),
                    t.Name == Model.SelectedDesign, t.Name == Model.OpenDesign, TileCommand)).ToList(), []));
        }
    }
}
