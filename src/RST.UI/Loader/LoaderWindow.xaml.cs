// LoaderWindow.xaml.cs — WPF host for the WebView2-rendered loader UI.
//
// Boot sequence:
//   1. Construct CoreWebView2Environment under %LocalAppData%\RST\WebView2
//      (writable; Revit's working dir isn't).
//   2. Map Assets/ as a virtual host so the legacy HTML loads its JS via
//      relative paths exactly as it did under pywebview.
//   3. Inject pywebview-shim.js at document_start.
//   4. Register LoaderBridge as host object "api".
//   5. Navigate to https://rst.ui/profile_loader.html.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using RST.Core.Scanning;

namespace RST.UI.Loader;

public partial class LoaderWindow : Window
{
    private const string VirtualHost = "rst.ui";
    private readonly LoaderBridge _bridge;

    public LoaderWindow(string revitVersion, IReadOnlyList<ScannedCommand> catalog)
    {
        InitializeComponent();
        _bridge = new LoaderBridge(revitVersion, catalog,
                                   () => Dispatcher.BeginInvoke(new Action(Close)));
        Loaded += async (_, _) => await InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var userDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RST", "WebView2");
            Directory.CreateDirectory(userDataDir);

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataDir);

            await WebView.EnsureCoreWebView2Async(env);
            var core = WebView.CoreWebView2;

            var assetsDir = Path.Combine(
                Path.GetDirectoryName(typeof(LoaderWindow).Assembly.Location)!,
                "Assets");

            core.SetVirtualHostNameToFolderMapping(
                hostName: VirtualHost,
                folderPath: assetsDir,
                accessKind: CoreWebView2HostResourceAccessKind.Allow);

            // File.ReadAllTextAsync is missing on net48; sync read is fine — the
            // shim file is ~2 KB and load happens once at window construction.
            var shim = File.ReadAllText(Path.Combine(assetsDir, "pywebview-shim.js"));
            await core.AddScriptToExecuteOnDocumentCreatedAsync(shim);

            core.AddHostObjectToScript("api", _bridge);

            core.Navigate($"https://{VirtualHost}/profile_loader.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Failed to initialise WebView2:\n\n" + ex.Message +
                "\n\nWebView2 Runtime must be installed (preinstalled on Win10 1903+/Win11).",
                "RST Loader",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }
}
