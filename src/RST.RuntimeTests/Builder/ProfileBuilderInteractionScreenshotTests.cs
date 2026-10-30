// rst-c's own (not vendored); the capture helpers follow ProfileBuilderScreenshotTests.
// AC3 for the Profile Builder's WU5 surfaces (spec #16, WU5): the icon picker overlay (grid with
// the current selection, then a design's variant row open under its tile's row), the RST Presets
// modal, the Junk Cleanup modal (and its empty state), and a drag in progress with the drop marker
// (DoDragDrop runs the OS drag loop, so the capture sets the drag state through the view-model the
// drag handlers drive). The keyboard test sends real key events to the picker overlay. The new
// dialogs stay out of the taskbar.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RST.Core.Builder;
using RST.Core.Profiles;
using RST.Core.Theming;
using RST.RuntimeTests.Loader;
using RST.RuntimeTests.Support;
using RST.UI.Builder;
using RST.UI.Controls;
using RST.UI.Dialogs;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.Builder;

[Collection(WpfThemeCollection.Name)]
public class ProfileBuilderInteractionScreenshotTests
{
    private static readonly Size BuilderSize = new(1450, 900);

    public static TheoryData<UiTheme> Themes => new() { UiTheme.Light, UiTheme.Dark };

    private static string Name(string what, UiTheme theme) => $"{what}-{theme.ToString().ToLowerInvariant()}";

    private static Color Palette(UiTheme theme, string key)
    {
        var c = UiPalette.For(theme)[key];
        return Color.FromRgb(c.R, c.G, c.B);
    }

    private static void Sta(Action action) => Screenshots.RunSta(() =>
    {
        try
        {
            action();
        }
        finally
        {
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });

    private static Color At(BitmapSource bitmap, Visual element, Visual root, double x, double y)
    {
        var p = element.TransformToAncestor(root).Transform(new Point(x, y));
        return Screenshots.PixelAt(bitmap, (int)Math.Floor(p.X), (int)Math.Floor(p.Y));
    }

    private static void AssertColor(Color expected, Color actual, string what) =>
        Assert.True(Math.Max(Math.Abs(expected.R - actual.R), Math.Max(Math.Abs(expected.G - actual.G), Math.Abs(expected.B - actual.B))) <= 2,
            $"{what}: expected {expected}, rendered {actual}");

    private static (ProfileBuilderWindow Window, BuilderHarness Harness) Open(Action<BuilderHarness>? arrange = null)
    {
        var h = BuilderHarness.Create();
        h.Model.SelectedProfileOption = h.Option("Structure");
        arrange?.Invoke(h);
        var window = new ProfileBuilderWindow(h.Model);
        WindowCapture.ShowOffScreen(window);
        return (window, h);
    }

    private static void Close(ProfileBuilderWindow window, BuilderHarness h)
    {
        h.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Secondary);
        window.Close();
    }

    private static BitmapSource CaptureDialog(Window dialog, UiTheme theme, string what)
    {
        WindowCapture.ShowOffScreen(dialog);
        try
        {
            var bitmap = WindowCapture.Client(dialog);
            Screenshots.Save(bitmap, Name(what, theme));
            AssertColor(Palette(theme, UiPalette.Window), Screenshots.PixelAt(bitmap, 1, 1), $"{what} corner");
            Assert.True(Screenshots.DistinctPixels(bitmap) > 30, $"{what} looks blank");
            return bitmap;
        }
        finally
        {
            dialog.Close();
        }
    }

    [Fact]
    public void The_new_dialogs_stay_out_of_the_taskbar() => Sta(() =>
    {
        RstTheme.Apply(UiTheme.Light);
        var presets = new PresetsWindow(new PresetsViewModel([], null, null));
        var cleanup = new CleanupWindow(new CleanupViewModel(null));
        Assert.False(presets.ShowInTaskbar, "RST Presets");
        Assert.False(cleanup.ShowInTaskbar, "Junk Cleanup Targets");
        Assert.Equal("RST Presets", presets.Title);
        Assert.Equal("Junk Cleanup Targets", cleanup.Title);
        Assert.Equal(WindowStartupLocation.CenterOwner, presets.WindowStartupLocation);
        Assert.Equal(WindowStartupLocation.CenterOwner, cleanup.WindowStartupLocation);
        presets.Close();
        cleanup.Close();
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_icon_picker_shows_its_grid_with_the_current_selection(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var (window, h) = Open(x => x.Model.Panels[0].Slots[0].PickIconCommand.Execute(null));
        try
        {
            WindowCapture.DoEvents();
            Assert.Equal(Visibility.Visible, window.IconPickerOverlay.Visibility);
            var bitmap = WindowCapture.Content(window, BuilderSize);
            Screenshots.Save(bitmap, Name("builder-icon-picker", theme));
            var tiles = WindowCapture.Descendants<Button>(window.IconPickerOverlay).Where(b => b.DataContext is IconTileViewModel).ToList();
            Assert.True(tiles.Count >= 21, $"the first rows of tiles are drawn ({tiles.Count})");
            Assert.True(tiles.Count(b => ((IconTileViewModel)b.DataContext).Thumbnail is not null) >= 20, "blue thumbnails");
            Assert.True(((IconTileViewModel)tiles[0].DataContext).IsNone);
            var selected = tiles.Single(b => ((IconTileViewModel)b.DataContext).IsSelected);
            Assert.Equal("parallel", ((IconTileViewModel)selected.DataContext).Design);

            // No tile is clipped: the last tile of the first row lies inside the scroll viewport (WU5 review B2).
            var presenter = WindowCapture.Descendants<ScrollContentPresenter>(window.IconPickerOverlay.RowsScroller).First();
            var last = tiles[IconPickerViewModel.Columns - 1];
            var bounds = last.TransformToAncestor(presenter).TransformBounds(new Rect(last.RenderSize));
            Assert.True(bounds.Left >= 0 && bounds.Right <= presenter.ActualWidth + 0.5,
                $"the 7th tile ({bounds.Left:0.#}..{bounds.Right:0.#}) fits the viewport (0..{presenter.ActualWidth:0.#})");
        }
        finally
        {
            h.Model.IconPicker?.CancelCommand.Execute(null);
            Close(window, h);
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_icon_picker_opens_the_variant_row_under_the_clicked_tile_s_row(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var (window, h) = Open(x =>
        {
            x.Model.Panels[0].Slots[0].PickIconCommand.Execute(null);
            x.Model.IconPicker!.TileCommand.Execute("parallel");
        });
        try
        {
            WindowCapture.DoEvents();
            var bitmap = WindowCapture.Content(window, BuilderSize);
            Screenshots.Save(bitmap, Name("builder-icon-picker-variants", theme));
            var rows = h.Model.IconPicker!.Rows;
            var variantRow = rows.IndexOf(rows.Single(r => r.IsVariantRow));
            var tileRow = rows.IndexOf(rows.First(r => r.Tiles.Any(t => t.Design == "parallel")));
            Assert.Equal(tileRow + 1, variantRow);
            var variants = WindowCapture.Descendants<Button>(window.IconPickerOverlay).Where(b => b.DataContext is IconVariantViewModel).ToList();
            Assert.Equal(IconPickerModel.PackColors, variants.Select(b => ((IconVariantViewModel)b.DataContext).Color));
            Assert.All(variants, b => Assert.True(b.IsEnabled));
            Assert.Equal("red", variants.Single(b => ((IconVariantViewModel)b.DataContext).IsSelected).Tag);
        }
        finally
        {
            h.Model.IconPicker?.CancelCommand.Execute(null);
            Close(window, h);
        }
    });

    [Fact]
    public void The_icon_picker_answers_the_keyboard_and_escape_closes_in_two_steps() => Sta(() =>
    {
        RstTheme.Apply(UiTheme.Light);
        var (window, h) = Open(x => x.Model.Panels[0].Slots[0].PickIconCommand.Execute(null));
        try
        {
            var overlay = window.IconPickerOverlay;
            window.Activate();
            Button? Focused() => FocusManager.GetFocusedElement(window) as Button ?? Keyboard.FocusedElement as Button;
            void Press(Key key)
            {
                var target = (IInputElement?)Focused() ?? overlay;
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                target.RaiseEvent(args);
                WindowCapture.DoEvents();
            }

            // Opening puts focus on the selected tile (WU5 review N3), so Escape works at once.
            WindowCapture.DoEvents();
            Assert.Equal("parallel", (Focused()?.DataContext as IconTileViewModel)?.Design);

            h.Model.IconPicker!.TileCommand.Execute("parallel");
            WindowCapture.DoEvents();
            var picker = h.Model.IconPicker!;
            Assert.Equal("red", picker.Model.FocusedColor);
            Assert.Equal("red", Focused()?.Tag);
            Press(Key.Left);
            Assert.Equal("orange", picker.Model.FocusedColor);
            Assert.Equal("orange", Focused()?.Tag);
            Press(Key.End);
            Assert.Equal("red", picker.Model.FocusedColor);

            Press(Key.Escape);
            Assert.True(h.Model.IsIconPickerOpen, "the first Escape closes the variant row only");
            Assert.Null(picker.Model.OpenDesign);
            Assert.Equal("parallel", (Focused()?.DataContext as IconTileViewModel)?.Design);
            Press(Key.Escape);
            Assert.False(h.Model.IsIconPickerOpen);
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            Assert.Equal("pack:parallel_red", h.Model.Profile.Panels[0].Slots[0].IconFile);
        }
        finally
        {
            Close(window, h);
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_presets_modal_renders(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var presets = new ProfilePresets { HiddenTabs = ["Massing & Site", "Collaborate", "Lumion"], DisableUnusedAddins = true };
        var dialog = new PresetsWindow(new PresetsViewModel(LoaderFixtures.Ribbon, presets, "Studio Struct"));
        Assert.True(((Button)dialog.FindName("ApplyButton")).IsDefault);
        CaptureDialog(dialog, theme, "builder-presets");

        var none = new PresetsWindow(new PresetsViewModel(["Modify", "View"], null, "T"));
        WindowCapture.ShowOffScreen(none);
        try
        {
            Assert.Contains(WindowCapture.Descendants<TextBlock>(none), t => t.IsVisible && t.Text == "No tabs available to hide on this machine.");
        }
        finally
        {
            none.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_cleanup_modal_renders_with_rows_and_empty(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var targets = CleanupDefaults.Build();
        targets[1].Enabled = false;
        targets[2].Name = "Revit Journals";
        var dialog = new CleanupWindow(new CleanupViewModel(targets));
        CaptureDialog(dialog, theme, "builder-cleanup");

        var model = new CleanupViewModel(targets);
        foreach (var row in model.Rows.ToList()) row.RemoveCommand.Execute(null);
        var empty = new CleanupWindow(model);
        WindowCapture.ShowOffScreen(empty);
        try
        {
            var text = WindowCapture.Descendants<TextBlock>(empty).Single(t => t.IsVisible && t.Text.StartsWith("No targets.", StringComparison.Ordinal));
            Assert.Equal("No targets. Use \"Restore Defaults\".", text.Text);
            Assert.DoesNotContain("Add Target", text.Text);
        }
        finally
        {
            empty.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void A_drag_in_progress_shows_its_marker_and_faded_source(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var (window, h) = Open(x =>
        {
            x.Model.BeginDrag(BuilderDrag.FromSlot(0, 0));
            x.Model.DragOverSlot(1, 1, upperHalf: true);
        });
        try
        {
            WindowCapture.DoEvents();
            var bitmap = WindowCapture.Content(window, BuilderSize);
            Screenshots.Save(bitmap, Name("builder-drag", theme));
            var marker = WindowCapture.Descendants<Border>(window.PanelCards)
                .Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "Builder.SlotDropMarker"
                             && b.DataContext is SlotRowViewModel { Panel.Index: 1, Index: 1 });
            Assert.Equal(Visibility.Visible, marker.Visibility);
            Assert.Equal(new Thickness(0, 2, 0, 0), marker.BorderThickness);
            AssertColor(Palette(theme, UiPalette.Success), At(bitmap, marker, window.Root, marker.ActualWidth / 2, 0.5), "drop-before line");
            var source = WindowCapture.Descendants<Grid>(window.PanelCards).Single(g => g.Name == "SlotRow" && g.DataContext is SlotRowViewModel { Panel.Index: 0, Index: 0 });
            Assert.Equal(0.4, source.Opacity, 3);
        }
        finally
        {
            h.Model.EndDrag();
            Close(window, h);
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_full_builder_after_picking_an_icon_and_configuring(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var (window, h) = Open(x =>
        {
            x.Model.Panels[1].Slots[2].PickIconCommand.Execute(null);
            x.Model.IconPicker!.TileCommand.Execute("move");
            x.Model.IconPicker!.VariantCommand.Execute("purple");
            x.Modals.EditPresetsWith = m => m.Tabs.Single(t => t.Tab == "Enscape").IsHidden = true;
            x.Model.ConfigurePresetsCommand.Execute(null);
            x.Model.Panels[1].SelectCommand.Execute(null);
        });
        try
        {
            var bitmap = WindowCapture.Content(window, BuilderSize);
            Screenshots.Save(bitmap, Name("builder-full", theme));
            Assert.Equal("RST — Profile Builder", window.Title);
            Assert.Equal("3 tabs hidden  •  Disable Unused: on", h.Model.PresetsSummary);
            Assert.True(window.ConfigurePresets.IsEnabled);
            // 7 pack icons; the Checks stack's 3 tools have none and draw the default 16 px icon (spec #17).
            var images = WindowCapture.Descendants<Image>(window.Preview).Where(i => i.Source is not null).ToList();
            Assert.Equal(7, images.Count(i => !ReferenceEquals(i.Source, PackIcons.Default16)));
            Assert.Equal(3, images.Count(i => ReferenceEquals(i.Source, PackIcons.Default16)));
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void A_click_inside_the_card_keeps_the_picker_and_a_click_on_the_scrim_cancels() => Sta(() =>
    {
        RstTheme.Apply(UiTheme.Light);
        var (window, h) = Open(x => x.Model.Panels[0].Slots[0].PickIconCommand.Execute(null));
        try
        {
            WindowCapture.DoEvents();
            var overlay = window.IconPickerOverlay;
            void Click(UIElement on) => on.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });

            Click(overlay.Card);
            Click(WindowCapture.Descendants<Border>(overlay.RowsScroller).First());
            Assert.True(h.Model.IsIconPickerOpen, "a click inside the card (Node: \"inside click does not\") keeps the picker open");

            Click(overlay.Scrim);
            Assert.False(h.Model.IsIconPickerOpen, "a click on the backdrop cancels");
            Assert.Equal("pack:parallel_red", h.Model.Profile.Panels[0].Slots[0].IconFile);
        }
        finally
        {
            Close(window, h);
        }
    });
}
