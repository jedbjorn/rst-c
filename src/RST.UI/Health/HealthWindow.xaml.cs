// HealthWindow.xaml.cs — WPF host for the WebView2-rendered health viewer.
//
// Same boot sequence as LoaderWindow:
//   1. Construct CoreWebView2Environment under %LocalAppData%\RST\WebView2.
//   2. Map Assets/ as a virtual host so health_viewer.html resolves
//      relative-path JS via https://rst.ui/.
//   3. Inject pywebview-shim.js at document_start (legacy JS calls
//      window.pywebview.api.foo_bar()).
//   4. Register HealthBridge as host object "api".
//   5. Navigate to https://rst.ui/health_viewer.html.

using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using RST.Core.Health;
using Serilog;

namespace RST.UI.Health;

public partial class HealthWindow : Window
{
    private const string VirtualHost = "rst.ui";
    private readonly HealthBridge _bridge;

    public HealthWindow(HealthContext context, Action<bool>? telemetryToggled = null,
                        Func<DateTimeOffset?>? liveSessionStartUtc = null)
    {
        InitializeComponent();
        _bridge = new HealthBridge(
            context,
            () => Dispatcher.BeginInvoke(new Action(Close)),
            telemetryToggled,
            liveSessionStartUtc);
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
                Path.GetDirectoryName(typeof(HealthWindow).Assembly.Location)!,
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
                    Log.Information("Health WebView2 navigation OK");
                else
                    Log.Warning("Health WebView2 navigation failed: status={Status}, error={Error}",
                                e.HttpStatusCode, e.WebErrorStatus);
            };

            var landing = $"https://{VirtualHost}/health_viewer.html";
            Log.Information("Health WebView2 initialised: assetsDir={AssetsDir}, userData={UserData}, landing={Landing}",
                            assetsDir, userDataDir, landing);
            core.Navigate(landing);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Health WebView2 initialisation failed");
            MessageBox.Show(
                "Failed to initialise WebView2:\n\n" + ex.Message +
                "\n\nWebView2 Runtime must be installed (preinstalled on Win10 1903+/Win11).",
                "RST Health",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }
}
