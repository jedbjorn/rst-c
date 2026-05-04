// LoaderHost.cs — entry point used by RST.Engine's LoaderCommand to open
// the loader UI without the Engine taking a direct dependency on WPF
// classes at the call site.

namespace RST.UI.Loader;

public static class LoaderHost
{
    /// <summary>
    /// Open the loader window modally. Blocks until the user closes it.
    /// Must be called on the Revit UI thread.
    /// </summary>
    public static void ShowModal(string revitVersion)
    {
        var window = new LoaderWindow(revitVersion);
        window.ShowDialog();
    }
}
