// rst-c's own (not vendored): the tray window's code-behind (spec #20 §1; F17 WU1). Built lazily by TrayHost the
// first time it is opened and rebuilt when the theme changes (Theme.xaml reads RstTheme when a window loads it).
// Closing it only closes the window; the tray keeps running.

using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using RST.UI.Dialogs;
using RST.UI.Theming;

namespace RST.Tray.Windows;

internal partial class TrayWindow : Window
{
    /// <summary>Build after <c>RstTheme.Apply</c>.</summary>
    public TrayWindow(TrayViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DarkTitleBar.Attach(this);
        Icon = WindowIcon();
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
        Closed += (_, _) =>
        {
            viewModel.RequestClose -= OnRequestClose;
            viewModel.Updates.Detach(); // the checker outlives the window
            viewModel.Health.Dispose(); // a scan or clean still running finishes in the background (HealthWork)
        };
    }

    public TrayViewModel ViewModel { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowPlacement.CapToWorkArea(this);
    }

    private void OnRequestClose(object? sender, EventArgs e) => Close();

    /// <summary>RST.msi's icon, embedded in the tray (null if it cannot be read: Windows' default icon then).</summary>
    internal static BitmapFrame? WindowIcon()
    {
        try
        {
            var bytes = TrayAssets.IconFile();
            return bytes.Length == 0 ? null : BitmapFrame.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>The tray's embedded assets.</summary>
internal static class TrayAssets
{
    /// <summary>rst.ico's bytes (empty when the resource is missing).</summary>
    public static byte[] IconFile()
    {
        using var stream = typeof(TrayAssets).Assembly.GetManifestResourceStream("RST.Tray.rst.ico");
        if (stream is null) return [];
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
