// LoaderWindow.xaml.cs — WPF host for the WebView2-rendered Profile Builder.
//
// Since spec #16 WU3 this window shows only profile_builder.html: the
// Profile Loader is the native ProfileLoaderWindow. The Builder stays on
// WebView2 until WU5.
//
// Boot sequence:
//   1. Construct CoreWebView2Environment under %LocalAppData%\RST\WebView2
//      (writable; Revit's working dir isn't).
//   2. Map Assets/ as a virtual host so the legacy HTML loads its JS via
//      relative paths exactly as it did under pywebview.
//   3. Inject pywebview-shim.js at document_start.
//   4. Register LoaderBridge as host object "api".
//   5. Navigate to https://rst.ui/profile_builder.html.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using RST.Core.Profiles;
using RST.Core.Scanning;
using Serilog;

namespace RST.UI.Loader;

public partial class LoaderWindow : Window
{
    private const string VirtualHost = "rst.ui";
    private const string LandingFile = "profile_builder.html";
    private readonly LoaderBridge _bridge;

    public LoaderWindow(string revitVersion,
                        IReadOnlyList<ScannedCommand> catalog,
                        IProfileSwitchScheduler? switchScheduler = null,
                        IReadOnlyList<string>? allTabs = null)
    {
        InitializeComponent();
        _bridge = new LoaderBridge(revitVersion, catalog,
                                   () => Dispatcher.BeginInvoke(new Action(Close)),
                                   switchScheduler,
                                   allTabs);
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

            var shimPath = Path.Combine(assetsDir, "pywebview-shim.js");
            var shim = File.ReadAllText(shimPath);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(shim);

            core.AddHostObjectToScript("api", _bridge);
            core.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess)
                    Log.Information("WebView2 navigation OK");
                else
                    Log.Warning("WebView2 navigation failed: status={Status}, error={Error}",
                                e.HttpStatusCode, e.WebErrorStatus);
            };

            var landing = $"https://{VirtualHost}/{LandingFile}";
            Log.Information("WebView2 initialised: assetsDir={AssetsDir}, userData={UserData}, landing={Landing}",
                            assetsDir, userDataDir, landing);
            core.Navigate(landing);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WebView2 initialisation failed");
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
