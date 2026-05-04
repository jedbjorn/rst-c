// LoaderHost.cs — entry point used by RST.Engine's LoaderCommand to open
// the loader UI without the Engine taking a direct dependency on WPF
// classes at the call site.

using System.Collections.Generic;
using RST.Core.Scanning;

namespace RST.UI.Loader;

public static class LoaderHost
{
    /// <summary>
    /// Open the loader window modally. Blocks until the user closes it.
    /// Must be called on the Revit UI thread.
    /// </summary>
    /// <param name="revitVersion">Revit major version (e.g. "2026").</param>
    /// <param name="catalog">Pre-built command catalog for the builder's
    /// tool picker. Pass an empty list when running outside a Revit
    /// session — the builder degrades to URL-only / hand-typed slots.</param>
    public static void ShowModal(string revitVersion, IReadOnlyList<ScannedCommand> catalog)
    {
        var window = new LoaderWindow(revitVersion, catalog);
        window.ShowDialog();
    }
}
