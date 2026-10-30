// rst-c's own (not vendored): the style gallery's sample data (spec #16 WU1, AC3).

using System.Windows;
using RST.Core.Colors;
using RST.UI.Controls;
using RST.UI.Swatches;
using RST.UI.Theming;

namespace RST.RuntimeTests.Gallery;

/// <summary>
/// Every Theme.xaml style in one window, for the light and dark gallery captures. Build it
/// after <see cref="RstTheme.Apply"/>: Theme.xaml reads the theme when the window loads it.
/// </summary>
public partial class StyleGalleryWindow : Window
{
    /// <summary>A 12-color sample palette for the embedded picker.</summary>
    public static readonly IReadOnlyList<Rgb> SamplePalette =
    [
        .. new[]
        {
            "#4F8EF7", "#2F6AD0", "#10B981", "#187534", "#F59E0B", "#B3261E",
            "#8B5CF6", "#EC4899", "#14B8A6", "#64748B", "#1F2328", "#FFFFFF",
        }.Select(Rgb.Parse),
    ];

    public StyleGalleryWindow()
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        var theme = RstTheme.Current.ToString().ToLowerInvariant();
        Heading.Text = $"RST style gallery — {theme}";

        ToastInfo.Content = Toast("Imported Architecture.zip", BannerKind.Info, "Profile Imported");
        ToastSuccess.Content = Toast("Saved & ribbon reloaded.", BannerKind.Success);
        ToastWarning.Content = Toast("Restart Revit to finish disabling 3 add-ins.", BannerKind.Warning, "Please Restart Revit");
        ToastError.Content = Toast("Button label required.", BannerKind.Error);

        var busy = new BusyState();
        busy.Show("Loading Architecture", "Applying the ribbon and add-in changes…");
        Overlay.Content = busy;

        Picker.DataContext = new ColorPickerViewModel(Rgb.Parse("#4F8EF7"), SamplePalette, new RecentColors(), _ => null);
    }

    private static TransientMessage Toast(string text, BannerKind kind, string? title = null)
    {
        // No timer: a gallery toast stays open.
        var toast = new TransientMessage((_, _) => throw new InvalidOperationException("no timer expected"));
        toast.Show(text, kind, Timeout.InfiniteTimeSpan, title);
        return toast;
    }
}
