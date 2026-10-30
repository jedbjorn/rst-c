// Copied from splash-it src/SplashIt.UI/Swatches/ColorPicker.xaml.cs (splash-it commit ba86a71; originally gantt-it src/GanttIt.Revit/UI/Swatches/ColorPicker.xaml.cs, gantt-it commit a96d0a1), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*).

using System.Windows.Controls;

namespace RST.UI.Swatches;

/// <summary>The custom color picker control (gantt-it §2.4). Bind its DataContext to a <see cref="ColorPickerViewModel"/>.</summary>
public partial class ColorPicker : UserControl
{
    public ColorPicker() => InitializeComponent();
}
