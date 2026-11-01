// rst-c's own (not vendored): the shared ribbon preview (spec #16, Shared controls; WU3 uses it in
// the Loader, WU4 in the Builder). Ports profile_loader.html renderTabPreview (1173-1217) and
// profile_builder.html renderPreview (2255-2307): panels tinted with the profile color at the
// effective opacity, tool slots with their pack icon (Planner default 9, both pages) or ⬜, stacks
// as columns of rows, "Empty" panels and "No panels configured". Builder mode labels tools by
// name and shows empty stacks as "(empty)". The canvas follows the window theme, not the OS.
// Stacks (feature #15, spec #17) draw as the ribbon draws them: a row per tool with its 16 px pack
// icon (or the ribbon's own default_16.png) and a one-line label. The Loader draws a stack only
// when the ribbon would (RibbonLayoutPlanner: 2 or 3 valid tools), with the tools the ribbon draws;
// the Builder draws each stack's tools as the profile holds them (it only opens profiles that pass
// validation, which applies the planner's rules, so the two agree).

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.Core.Ribbon;
using RST.UI.Loader;
using RST.UI.Mvvm;
using RST.UI.Resources;
using RST.UI.Theming;
using ProfilePanel = RST.Core.Profiles.Panel;

namespace RST.UI.Controls;

/// <summary>
/// A read-only picture of a profile's ribbon tab. Set <see cref="Profile"/> (and, in the Loader,
/// <see cref="OverrideOpacity"/>); it redraws when either changes. The profile is a plain object
/// without change notifications, so a live editor (the Builder) bumps <see cref="Revision"/> or
/// calls <see cref="Refresh"/> after every edit.
/// </summary>
public partial class RibbonPreview : UserControl
{
    public static readonly DependencyProperty ProfileProperty = DependencyProperty.Register(
        nameof(Profile), typeof(Profile), typeof(RibbonPreview), new PropertyMetadata(null, Redraw));

    public static readonly DependencyProperty OverrideOpacityProperty = DependencyProperty.Register(
        nameof(OverrideOpacity), typeof(int?), typeof(RibbonPreview), new PropertyMetadata(null, Redraw));

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(RibbonPreviewMode), typeof(RibbonPreview), new PropertyMetadata(RibbonPreviewMode.Loader, Redraw));

    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(
        nameof(Revision), typeof(int), typeof(RibbonPreview), new PropertyMetadata(0, Redraw));

    public static readonly DependencyProperty TopBarContentProperty = DependencyProperty.Register(
        nameof(TopBarContent), typeof(object), typeof(RibbonPreview), new PropertyMetadata(null));

    public static readonly DependencyProperty PackDirectoryProperty = DependencyProperty.Register(
        nameof(PackDirectory), typeof(string), typeof(RibbonPreview), new PropertyMetadata(null, Redraw));

    /// <summary>Revit's ribbon background, dark theme (spec #16: the canvas follows the window theme).</summary>
    public static readonly Color DarkCanvas = Color.FromRgb(0x2D, 0x2D, 0x30);

    /// <summary>Revit's ribbon background, light theme.</summary>
    public static readonly Color LightCanvas = Color.FromRgb(0xE1, 0xE1, 0xE1);

    private readonly bool _dark;
    private readonly RibbonColors _colors;

    public RibbonPreview()
    {
        InitializeComponent();
        _dark = RstTheme.IsDark;
        _colors = _dark ? RibbonColors.Dark : RibbonColors.Light;
        CanvasBorder.Background = Frozen(_dark ? DarkCanvas : LightCanvas);
        Rebuild();
    }

    /// <summary>The profile to draw; null draws an empty canvas under a "—" tab.</summary>
    public Profile? Profile
    {
        get => (Profile?)GetValue(ProfileProperty);
        set => SetValue(ProfileProperty, value);
    }

    /// <summary>The opacity to draw with (10-100); null uses the profile's own.</summary>
    public int? OverrideOpacity
    {
        get => (int?)GetValue(OverrideOpacityProperty);
        set => SetValue(OverrideOpacityProperty, value);
    }

    /// <summary>The Loader's preview or the Builder's (labels by name, "(empty)" stacks).</summary>
    public RibbonPreviewMode Mode
    {
        get => (RibbonPreviewMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>Bump to redraw after editing <see cref="Profile"/> in place (the Builder's live preview).</summary>
    public int Revision
    {
        get => (int)GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    /// <summary>The page's controls at the right of the tab pill (the Loader's opacity override and Read Only badge).</summary>
    public object? TopBarContent
    {
        get => GetValue(TopBarContentProperty);
        set => SetValue(TopBarContentProperty, value);
    }

    /// <summary>The icon pack folder; null is the shipped Assets\icons\pack next to RST.UI.dll.</summary>
    public string? PackDirectory
    {
        get => (string?)GetValue(PackDirectoryProperty);
        set => SetValue(PackDirectoryProperty, value);
    }

    /// <summary>True when the canvas uses Revit's dark ribbon color.</summary>
    public bool IsDarkCanvas => _dark;

    /// <summary>The drawn panels (one element per profile panel), for tests.</summary>
    internal UIElementCollection DrawnPanels => PanelsHost.Children;

    /// <summary>Redraws now (after an in-place edit of <see cref="Profile"/>).</summary>
    public void Refresh() => Rebuild();

    // A property change from a binding: WPF would swallow an exception here without a trace (SC-046).
    private static void Redraw(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        UiGuard.Run("Ribbon preview: redraw", ((RibbonPreview)d).Rebuild);

    private void Rebuild()
    {
        if (PanelsHost is null) return;
        PanelsHost.Children.Clear();
        var profile = Profile;
        TabPillText.Text = string.IsNullOrEmpty(profile?.Tab) ? UiStrings.Preview_NoTab : profile!.Tab;
        if (profile is null) return;

        if (profile.Panels.Count == 0)
        {
            PanelsHost.Children.Add(new TextBlock
            {
                Text = Mode == RibbonPreviewMode.Builder ? UiStrings.Preview_NoPanelsBuilder : UiStrings.Preview_NoPanels,
                Foreground = Frozen(_colors.Muted),
                Margin = new Thickness(6, 8, 6, 8),
            });
            return;
        }

        var opacity = RibbonPreviewRules.EffectiveOpacity(profile, OverrideOpacity);
        foreach (var panel in profile.Panels)
        {
            if (panel is null) continue;
            PanelsHost.Children.Add(BuildPanel(profile, panel, opacity));
        }
    }

    private FrameworkElement BuildPanel(Profile profile, ProfilePanel panel, int opacity)
    {
        var hex = Mode == RibbonPreviewMode.Builder && string.IsNullOrEmpty(panel.Color) ? RibbonPreviewRules.DefaultPanelColor : panel.Color;
        var tint = RibbonPreviewRules.PanelColor(hex, opacity);

        var slots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 8, 8, 6), MinHeight = 58 };
        foreach (var slot in panel.Slots)
        {
            if (slot is null) continue;
            if (slot.SlotType == "tool") slots.Children.Add(BuildTool(slot));
            else if (slot.SlotType == "stack" && BuildStack(profile, slot) is { } stack) slots.Children.Add(stack);
        }
        if (slots.Children.Count == 0)
        {
            slots.Children.Add(new TextBlock
            {
                Text = UiStrings.Preview_Empty,
                Foreground = Frozen(_colors.Muted),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(24, 0, 24, 0),
            });
            slots.VerticalAlignment = VerticalAlignment.Stretch;
        }

        var name = new Border
        {
            Background = Frozen(Color.FromArgb(0x2E, 0, 0, 0)),
            Padding = new Thickness(8, 4, 8, 4),
            Child = new TextBlock
            {
                Text = panel.Name ?? "",
                Foreground = Frozen(_colors.Muted),
                FontSize = 10,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        DockPanel.SetDock(name, Dock.Bottom);

        var body = new DockPanel { LastChildFill = true };
        body.Children.Add(name);
        body.Children.Add(slots);

        var card = new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = Frozen(Color.FromArgb(tint.Alpha, tint.R, tint.G, tint.B)),
            BorderBrush = Frozen(Color.FromArgb(0x26, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            MinWidth = 100,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = true,
            Child = body,
            Tag = panel,
        };
        System.Windows.Automation.AutomationProperties.SetName(card, panel.Name ?? "");
        return card;
    }

    private FrameworkElement BuildTool(Slot slot)
    {
        var icon = PackIcons.ForSlot(slot, PackDirectory, dark: _dark);
        var box = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(3),
            Background = Frozen(_colors.IconBox),
            BorderBrush = Frozen(_colors.IconBoxBorder),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = icon is not null
                ? new Image { Source = icon, Width = 20, Height = 20, Stretch = Stretch.Uniform }
                : new TextBlock
                {
                    Text = "⬜",
                    FontSize = 11,
                    Foreground = Frozen(_colors.Muted),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
        };
        RenderOptions.SetBitmapScalingMode(box, BitmapScalingMode.HighQuality);
        var label = RibbonPreviewRules.ToolLabel(slot, Mode);
        var stack = new StackPanel { Width = 52, Margin = new Thickness(0, 0, 5, 0), ToolTip = string.IsNullOrEmpty(label) ? null : label };
        stack.Children.Add(box);
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Frozen(_colors.Text),
            FontSize = 10,
            Margin = new Thickness(0, 3, 0, 0),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return stack;
    }

    private FrameworkElement? BuildStack(Profile profile, Slot slot)
    {
        var tools = profile.Stacks.TryGetValue(slot.Name ?? "", out var def) && def is not null ? def.Tools : [];
        var planned = RibbonLayoutPlanner.PlanStack(profile, slot).Item;
        var column = new StackPanel { MinWidth = 84, MaxWidth = 150, Margin = new Thickness(0, 0, 5, 0), Tag = slot };
        if (Mode != RibbonPreviewMode.Builder)
        {
            // The Loader shows what the ribbon shows: nothing for a stack the ribbon skips.
            if (planned is null) return null;
            foreach (var button in planned.Buttons) column.Children.Add(StackRow(button.Label, button.Source, dim: false));
            return column;
        }
        if (tools.Count == 0)
        {
            // The Builder keeps a stub row so an empty stack stays visible.
            column.Children.Add(StackRow(UiStrings.Preview_EmptyStack(slot.Name), null, dim: true));
            return column;
        }
        foreach (var tool in tools)
            if (tool is not null) column.Children.Add(StackRow(RibbonLayoutPlanner.SingleLine(tool.Name), tool, dim: false));
        return column;
    }

    private Border StackRow(string text, Slot? tool, bool dim)
    {
        // A tool: its pack or companion icon at 16 px, else the ribbon's default_16.png, as the stacked button
        // draws it. The "≡" box only for an empty stack's stub row, or when the default is missing.
        var icon = tool is null ? null
            : PackIcons.ForSlot(tool, PackDirectory, 16, _dark) ?? PackIcons.Default16;
        FrameworkElement glyph = icon is not null
            ? new Image
            {
                Source = icon,
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            }
            : new Border
            {
                Width = 13,
                Height = 13,
                CornerRadius = new CornerRadius(2),
                BorderBrush = Frozen(_colors.StackBorder),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 4, 0),
                Child = new TextBlock
                {
                    Text = "≡",
                    FontSize = 9,
                    Foreground = Frozen(_colors.StackGlyph),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
        var row = new DockPanel();
        row.Children.Add(glyph);
        row.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Frozen(_colors.Text),
            FontSize = 10,
            Opacity = dim ? 0.5 : 1,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return new Border
        {
            Background = Frozen(_colors.IconBox),
            BorderBrush = Frozen(_colors.StackBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 3, 5, 3),
            Margin = new Thickness(0, 0, 0, 2),
            Child = row,
            ToolTip = text,
        };
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>The ribbon's own colors: they follow the canvas (Revit's ribbon), not the window palette.</summary>
    private sealed record RibbonColors(Color Text, Color Muted, Color IconBox, Color IconBoxBorder, Color StackBorder, Color StackGlyph)
    {
        public static readonly RibbonColors Dark = new(
            Color.FromRgb(0xE4, 0xE4, 0xE4), Color.FromRgb(0xB0, 0xB0, 0xB0), Color.FromRgb(0x3C, 0x3C, 0x41),
            Color.FromRgb(0x5A, 0x5A, 0x60), Color.FromArgb(0x66, 0x7C, 0x3A, 0xED), Color.FromRgb(0xA7, 0x8B, 0xFA));

        public static readonly RibbonColors Light = new(
            Color.FromRgb(0x22, 0x22, 0x22), Color.FromRgb(0x4A, 0x4A, 0x4A), Color.FromRgb(0xFA, 0xFA, 0xFA),
            Color.FromRgb(0xB4, 0xB4, 0xB4), Color.FromArgb(0x66, 0x7C, 0x3A, 0xED), Color.FromRgb(0x6D, 0x28, 0xD9));
    }
}
