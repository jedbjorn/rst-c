// Copied from splash-it src/SplashIt.UI/Swatches/ColorPickerDialog.xaml.cs (splash-it commit ba86a71; originally gantt-it src/GanttIt.Revit/UI/Swatches/ColorPickerDialog.xaml.cs, gantt-it commit a96d0a1), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); comments say RST.

using System.Windows;
using System.Windows.Interop;
using RST.Core.Colors;
using RST.UI.Dialogs;
using RST.UI.Theming;

namespace RST.UI.Swatches;

/// <summary>Hosts the <see cref="ColorPicker"/> as a small modal window opened from a color chip.</summary>
public partial class ColorPickerDialog : Window
{
    /// <param name="title">The window title, naming the chip being edited ("Project color 3").</param>
    /// <param name="anchor">Screen position (DIPs) to open next to, usually the pointer; null centres on the owner.</param>
    /// <param name="workArea">Work area (DIPs) of the owner's monitor; the picker is kept inside it.</param>
    public ColorPickerDialog(Rgb current, IReadOnlyList<Rgb> palette, string title, Point? anchor, Rect workArea)
    {
        InitializeComponent();
        Title = title;
        DarkTitleBar.Attach(this);
        var model = new ColorPickerViewModel(current, palette, PickerSession.RecentColors, MoreColors, PickFromScreen);
        model.Picked += color =>
        {
            Result = color;
            DialogResult = true;
        };
        DataContext = model;

        if (anchor is { } p)
        {
            // Keep the whole picker on the owner's monitor.
            var area = workArea;
            Left = Math.Max(area.Left, Math.Min(p.X - 20, area.Right - 250));
            Top = Math.Max(area.Top, Math.Min(p.Y + 14, area.Bottom - 380));
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
    }

    public Rgb? Result { get; private set; }

    private Rgb? MoreColors(Rgb current) => NativeColorDialog.Show(new WindowInteropHelper(this).Handle, current);

    /// <summary>The eyedropper: hides this picker and the RST window while the user picks a pixel.</summary>
    private Rgb? PickFromScreen() => ScreenColorPicker.Pick(this);
}
