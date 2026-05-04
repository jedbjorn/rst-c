// FileDialogBridge.cs — UI-thread-safe wrapper around OpenFileDialog.
//
// LoaderBridge.AddProfile is invoked from a WebView2 host-object call,
// which dispatches on a worker thread. Microsoft.Win32.OpenFileDialog
// requires the WPF UI thread, so we marshal via Application.Current.
// Returns null on cancel or when no Application is available (e.g.
// running outside Revit).

using System.Windows;
using Microsoft.Win32;

namespace RST.UI.Loader;

internal static class FileDialogBridge
{
    public static string? OpenJson()
    {
        var app = Application.Current;
        if (app is null) return null;

        return (string?)app.Dispatcher.Invoke(new System.Func<string?>(() =>
        {
            var dlg = new OpenFileDialog
            {
                Title = "Import RST Profile",
                Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                Multiselect = false,
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }));
    }

    public static string? OpenImage()
    {
        var app = Application.Current;
        if (app is null) return null;

        return (string?)app.Dispatcher.Invoke(new System.Func<string?>(() =>
        {
            var dlg = new OpenFileDialog
            {
                Title = "Choose Logo Image",
                Filter = "Image Files (*.png;*.jpg;*.jpeg;*.svg;*.bmp)|*.png;*.jpg;*.jpeg;*.svg;*.bmp|All Files (*.*)|*.*",
                Multiselect = false,
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }));
    }

    public static string? SaveJson(string suggestedFileName)
    {
        var app = Application.Current;
        if (app is null) return null;

        return (string?)app.Dispatcher.Invoke(new System.Func<string?>(() =>
        {
            var dlg = new SaveFileDialog
            {
                Title = "Export RST Profile",
                Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                FileName = suggestedFileName,
                DefaultExt = ".json",
                AddExtension = true,
                OverwritePrompt = true,
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }));
    }
}
