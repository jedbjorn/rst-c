// rst-c's own (not vendored): maps the Core ribbon layout (RST.Core.Ribbon.RibbonLayoutPlanner,
// feature #15, spec #17) onto AdWindows items.
//   - A large button: RibbonButton, Size Large, vertical, wrapped label, the 32 px icon as both
//     Image and LargeImage (AdWindows draws a Large button with no image as a split-button chevron).
//   - A stack: RibbonRowPanel holding Standard-size RibbonButtons, horizontal, one-line labels,
//     the 16 px icon as Image (the 32 px as LargeImage, for the Quick Access Toolbar), separated by
//     RibbonRowBreak, so each button is a row: what UIControlledApplication's AddStackedItems builds,
//     which only works during OnStartup and so cannot serve a live profile switch (RST-020).
//
// Every button dispatches through the ICommand the caller gives (SlotInvokeCommand in Revit). The
// factory touches only AdWindows and WPF types, never the Revit API, so RST.RuntimeTests can build
// the items outside Revit (spec #17, T-A7).

using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RST.Core.Ribbon;
using AwRibbonButton = Autodesk.Windows.RibbonButton;
using AwRibbonItemSize = Autodesk.Windows.RibbonItemSize;
using AwRibbonRowBreak = Autodesk.Windows.RibbonRowBreak;
using AwRibbonRowPanel = Autodesk.Windows.RibbonRowPanel;

namespace RST.Engine.Ribbon;

internal static class RibbonItemFactory
{
    /// <summary>A large button (a panel tool slot).</summary>
    /// <param name="button">The planned button (<see cref="PlannedButtonSize.Large"/>).</param>
    /// <param name="icon">The 32 px icon.</param>
    /// <param name="handler">What a click runs.</param>
    public static AwRibbonButton LargeButton(PlannedButton button, ImageSource? icon, ICommand handler)
    {
        if (button is null) throw new ArgumentNullException(nameof(button));
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        return new AwRibbonButton
        {
            Text = button.Label,
            Id = button.Id,
            ShowText = true,
            ShowImage = true,
            Size = AwRibbonItemSize.Large,
            Image = icon,
            LargeImage = icon,
            Orientation = Orientation.Vertical,
            ToolTip = button.Target.ToolTip,
            CommandHandler = handler,
        };
    }

    /// <summary>A small button inside a stack.</summary>
    /// <param name="button">The planned button (<see cref="PlannedButtonSize.Small"/>).</param>
    /// <param name="icon16">The 16 px icon (the button's Image).</param>
    /// <param name="icon32">The 32 px icon (LargeImage, used if the button is pinned to the Quick Access Toolbar large).</param>
    /// <param name="handler">What a click runs.</param>
    public static AwRibbonButton SmallButton(PlannedButton button, ImageSource? icon16, ImageSource? icon32, ICommand handler)
    {
        if (button is null) throw new ArgumentNullException(nameof(button));
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        return new AwRibbonButton
        {
            Text = button.Label,
            Id = button.Id,
            ShowText = true,
            ShowImage = true,
            Size = AwRibbonItemSize.Standard,
            Image = icon16,
            LargeImage = icon32 ?? icon16,
            Orientation = Orientation.Horizontal,
            ToolTip = button.Target.ToolTip,
            CommandHandler = handler,
        };
    }

    /// <summary>
    /// A stack: a row panel of its 2 or 3 small buttons, a <c>RibbonRowBreak</c> between each, so they
    /// draw one per row like Revit's stacked items.
    /// </summary>
    /// <param name="stack">The planned stack.</param>
    /// <param name="icon16">The 16 px icon of a button.</param>
    /// <param name="icon32">The 32 px icon of a button.</param>
    /// <param name="handler">The click handler of a button (one per button).</param>
    public static AwRibbonRowPanel Stack(
        StackItem stack,
        Func<PlannedButton, ImageSource?> icon16,
        Func<PlannedButton, ImageSource?> icon32,
        Func<PlannedButton, ICommand> handler)
    {
        if (stack is null) throw new ArgumentNullException(nameof(stack));
        if (icon16 is null) throw new ArgumentNullException(nameof(icon16));
        if (icon32 is null) throw new ArgumentNullException(nameof(icon32));
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        if (stack.Buttons.Count is < RibbonLayoutPlanner.MinStackItems or > RibbonLayoutPlanner.MaxStackItems)
            throw new ArgumentException($"a stack holds {RibbonLayoutPlanner.MinStackItems}-{RibbonLayoutPlanner.MaxStackItems} buttons (got {stack.Buttons.Count})", nameof(stack));

        var row = new AwRibbonRowPanel { Id = stack.Id };
        for (int i = 0; i < stack.Buttons.Count; i++)
        {
            if (i > 0) row.Items.Add(new AwRibbonRowBreak());
            var b = stack.Buttons[i];
            row.Items.Add(SmallButton(b, icon16(b), icon32(b), handler(b)));
        }
        return row;
    }

    /// <summary>The buttons of a row panel built by <see cref="Stack"/>, in row order (the breaks left out).</summary>
    public static IReadOnlyList<AwRibbonButton> ButtonsOf(AwRibbonRowPanel row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        var buttons = new List<AwRibbonButton>();
        foreach (var item in row.Items)
            if (item is AwRibbonButton b) buttons.Add(b);
        return buttons;
    }
}
