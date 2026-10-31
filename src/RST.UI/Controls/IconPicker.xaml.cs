// rst-c's own (not vendored): the icon picker overlay's view plumbing (spec #16, WU5). Behavior is
// in IconPickerViewModel / RST.Core's IconPickerModel; here: Escape (two-stage), the variant row's
// arrow, Home and End keys, the backdrop click, and moving keyboard focus to the model's focused
// variant or tile after each change (the rows are rebuilt, so focus is placed again).

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RST.Core.Builder;
using RST.UI.Mvvm;

namespace RST.UI.Controls;

public partial class IconPicker : UserControl
{
    private IconPickerViewModel? _model;

    public IconPicker()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => UiGuard.Run("Icon picker: open", () =>
        {
            if (_model is not null) _model.Changed -= PlaceFocus;
            _model = e.NewValue as IconPickerViewModel;
            if (_model is null) return;
            _model.Changed += PlaceFocus;
            // Opening: focus the selected tile (or the first one).
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                UiGuard.Run("Icon picker: focus", () => FocusTile(_model?.Model.SelectedDesign, fallbackFirst: true)));
        });
        PreviewKeyDown += OnKey;
    }

    private void OnScrimDown(object sender, MouseButtonEventArgs e) => UiGuard.Run("Icon picker: click outside", () =>
    {
        _model?.OutsideClick();
        e.Handled = true;
    });

    private void OnKey(object sender, KeyEventArgs e) => UiGuard.Run("Icon picker: key", () => HandleKey(e));

    private void HandleKey(KeyEventArgs e)
    {
        if (_model is null) return;
        if (e.Key == Key.Escape)
        {
            _model.Escape();
            e.Handled = true;
            return;
        }
        if (_model.Model.OpenDesign is null || _model.Model.FocusedColor is null) return;
        PickerKey? key = e.Key switch
        {
            Key.Left => PickerKey.Left,
            Key.Right => PickerKey.Right,
            Key.Up => PickerKey.Up,
            Key.Down => PickerKey.Down,
            Key.Home => PickerKey.Home,
            Key.End => PickerKey.End,
            _ => null,
        };
        // Typing never reaches the picker (it has no text box), so the keys always rove the open row.
        if (key is null) return;
        _model.Key(key.Value);
        e.Handled = true;
    }

    private void PlaceFocus() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => UiGuard.Run("Icon picker: move focus", () =>
    {
        if (_model is null) return;
        if (_model.Model.FocusedColor is { } color) FocusVariant(color);
        else if (_model.Model.FocusedTile is { } tile) FocusTile(tile, fallbackFirst: false);
    }));

    private void FocusVariant(string color) =>
        Buttons().FirstOrDefault(b => b.DataContext is IconVariantViewModel v && v.Color == color)?.Focus();

    private void FocusTile(string? design, bool fallbackFirst)
    {
        var tiles = Buttons().Where(b => b.DataContext is IconTileViewModel).ToList();
        var target = tiles.FirstOrDefault(b => ((IconTileViewModel)b.DataContext).Design == design && design is not null)
                     ?? (fallbackFirst ? tiles.FirstOrDefault() : null);
        target?.Focus();
    }

    private IEnumerable<Button> Buttons() => Descendants<Button>(RowsHost);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }
}
