// rst-c's own (not vendored): the tray window's code-behind (spec #20 §1; F17 WU1). Built lazily by TrayHost the
// first time it is opened and rebuilt when the theme changes (Theme.xaml reads RstTheme when a window loads it).
// Closing it only closes the window; the tray keeps running. Escape is the window's Close (its IsCancel button), except
// while a model's detail shows on Health › Models (spec #25 WU4): then a fresh Escape nothing else used goes Back to the
// list, wherever the keyboard is in the window (the detail, the tabs, the footer), and never closes the window.
// SC-048 item 2: the notification area has its own small-size icon; the window keeps the MSI icon.

using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using RST.UI.Dialogs;
using RST.UI.Mvvm;
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
        KeyDown += OnKeyDown;
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

    // Bubbling, so a press a control inside used (a drop-down closing its list) is left alone; marked handled, so the
    // Close button's IsCancel (the access-key pass after routing) never sees it.
    private void OnKeyDown(object sender, KeyEventArgs e) => UiGuard.Run("tray: Escape", () =>
    {
        if (e.Handled || e.Key != Key.Escape || e.IsRepeat) return;
        if (!ViewModel.IsHealthPage || !ViewModel.Health.IsDetailShowing) return;
        e.Handled = true;
        ViewModel.Health.Detail.Back();
    });

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
        return Read("RST.Tray.rst.ico");
    }

    /// <summary>The high-contrast notification icon, with 16–48 px entries.</summary>
    public static byte[] NotificationIconFile() => Read("RST.Tray.rst-tray.ico");

    private static byte[] Read(string name)
    {
        using var stream = typeof(TrayAssets).Assembly.GetManifestResourceStream(name);
        if (stream is null) return [];
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
