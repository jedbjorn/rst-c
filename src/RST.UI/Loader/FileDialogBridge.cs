// FileDialogBridge.cs — UI-thread-safe wrapper around the Windows file dialogs.
//
// LoaderBridge's Builder methods (pick_logo_file, export_profile) are invoked
// from a WebView2 host-object call, which dispatches on a worker thread.
// Microsoft.Win32 file dialogs require the WPF UI thread, so we marshal via
// Application.Current. Returns null on cancel or when no Application is
// available (e.g. running outside Revit). The native Profile Loader opens its
// import dialog through IDialogService instead (spec #16, WU3), and the WPF Builder
// its logo and export dialogs, with the same titles and filters (WU4).

using System.Windows;
using Microsoft.Win32;

namespace RST.UI.Loader;

internal static class FileDialogBridge
{
    /// <summary>The Profile Loader's import dialog title and filter (kept for the WPF Loader).</summary>
    public const string ImportZipTitle = "Import RST Profile (.zip)";

    public const string ZipFilter = "RST Profile Package (*.zip)|*.zip|All Files (*.*)|*.*";

    /// <summary>The Builder's logo and export dialogs (kept for the WPF Builder, spec #16 WU4).</summary>
    public const string LogoTitle = "Choose Logo Image";

    public const string ImageFilter = "Image Files (*.png;*.jpg;*.jpeg;*.svg;*.bmp)|*.png;*.jpg;*.jpeg;*.svg;*.bmp|All Files (*.*)|*.*";

    public const string ExportZipTitle = "Export RST Profile (.zip)";

    public static string? OpenImage()
    {
        var app = Application.Current;
        if (app is null) return null;

        return (string?)app.Dispatcher.Invoke(new System.Func<string?>(() =>
        {
            var dlg = new OpenFileDialog
            {
                Title = LogoTitle,
                Filter = ImageFilter,
                Multiselect = false,
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }));
    }

    public static string? SaveZip(string suggestedFileName)
    {
        var app = Application.Current;
        if (app is null) return null;

        return (string?)app.Dispatcher.Invoke(new System.Func<string?>(() =>
        {
            var dlg = new SaveFileDialog
            {
                Title = ExportZipTitle,
                Filter = ZipFilter,
                FileName = suggestedFileName,
                DefaultExt = ".zip",
                AddExtension = true,
                OverwritePrompt = true,
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }));
    }
}
