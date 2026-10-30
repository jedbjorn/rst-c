// rst-c's own (not vendored): the titles and filters of rst-c's file dialogs (spec #16), kept
// from the WebView2-era FileDialogBridge (retired in WU5) so the Loader's import and the Builder's
// logo and export dialogs read exactly as before. The dialogs open through IDialogService.

namespace RST.UI.Dialogs;

public static class FileDialogTexts
{
    /// <summary>The profile package filter (Loader import, Builder export).</summary>
    public const string ZipFilter = "RST Profile Package (*.zip)|*.zip|All Files (*.*)|*.*";

    /// <summary>The Builder's logo dialog.</summary>
    public const string LogoTitle = "Choose Logo Image";

    public const string ImageFilter = "Image Files (*.png;*.jpg;*.jpeg;*.svg;*.bmp)|*.png;*.jpg;*.jpeg;*.svg;*.bmp|All Files (*.*)|*.*";

    /// <summary>The Builder's Export ZIP dialog.</summary>
    public const string ExportZipTitle = "Export RST Profile (.zip)";
}
