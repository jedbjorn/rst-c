// rst-c's own (not vendored): the RST tray's coordinator (spec #20 §1; F17 WU1). It owns the icon, the one window
// (built lazily on first open, rebuilt on a theme change, released when closed), the settings and the first-run
// update-check notice, and it ends the process cleanly on Quit, on WM_CLOSE (Windows Installer's
// util:CloseApplication) and at sign-out. Event-driven: no polling. Besides the icon's short add retries at sign-in,
// its one timer is the update checker's (F17 WU3, TrayUpdates): 2–10 minutes after start, then daily; off sends nothing.
// Installs (F17 WU4, InstallCoordinator) run only on a click: verified download, never while Revit runs, msiexec for
// a companion, and for RST itself the updater copy (SelfUpdater), after which the tray quits.

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using RST.Core.Configuration;
using RST.Core.Tray;
using RST.Core.Updates;
using RST.Tray.Resources;
using RST.Tray.Shell;
using RST.Tray.Updates;
using RST.Tray.Windows;
using RST.UI.Dialogs;
using RST.UI.Theming;
using Serilog;

namespace RST.Tray;

internal sealed class TrayHost : ITraySettingsHost, ITrayUpdatesHost, IDisposable
{
    public const string NoticeAutomationId = "TrayUpdateNotice";

    public const string ShadowNoticeAutomationId = "TrayShadowNotice";

    private readonly TrayArguments _args;
    private readonly SingleInstance _instance;
    private readonly Dispatcher _dispatcher;
    private TraySettings _settings = new();
    private UpdateChecksState _checks;
    private TrayIcon? _icon;
    private HttpClient? _http;
    private TrayUpdates? _updates;
    private InstallCoordinator? _installs;
    private RevitWatch? _revit;
    private SelfUpdater? _selfUpdater;
    private CancellationTokenSource? _revitPageWatch;
    private IReadOnlyList<ProductStatus>? _pendingAnnounce;
    private TrayWindow? _window;
    private TrayPage _page = TrayPage.Updates;
    private bool _quitting;
    private int _iconFailures;
    private DispatcherTimer? _iconRetry;

    public TrayHost(TrayArguments args, SingleInstance instance)
    {
        _args = args ?? throw new ArgumentNullException(nameof(args));
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public TrayThemeChoice ThemeChoice => _settings.Theme;

    public UpdateChecksState UpdateChecks => _checks;

    public string Version { get; } = CurrentVersion();

    public string LogFolder => TrayPaths.LogsDir;

    /// <summary>Why the tray ended, for the exit log line.</summary>
    public string ExitReason { get; private set; } = "dispatcher stopped";

    // ITrayUpdatesHost (F17 WU3): the Updates and Apps pages read the checker through the host.

    public UpdateSnapshot Current => _updates?.Current ?? UpdateSnapshot.Empty(ProductCatalog.Default, _checks.Enabled);

    /// <summary>The checker's snapshot or an install changed (on the UI thread).</summary>
    public event EventHandler? Changed;

    // F17 WU4: installs.

    public IReadOnlyDictionary<string, InstallProgress> Installs => _installs?.Current ?? ITrayUpdatesHost.EmptyInstalls;

    public bool RevitRunning => _installs?.RevitRunning ?? false;

    public bool PerMachineTray => _selfUpdater?.PerMachineTray ?? false;

    /// <summary>Install or Update: the product's latest version, through every check (InstallCoordinator). Never blocks.</summary>
    public void Install(ProductInfo product)
    {
        if (_quitting || _installs is null) return;
        var status = Current.Products.FirstOrDefault(p => p.Product.Id == product.Id);
        if (status?.Latest is not { } latest)
        {
            Log.Warning("install {Product}: no latest version known yet", product.Name);
            return;
        }
        Log.Information("install {Product} {Version} (asked by the user)", product.Name, latest);
        _ = _installs.InstallAsync(product, latest);
    }

    public void ReinstallPrevious(ProductInfo product)
    {
        if (_quitting || _installs is null) return;
        Log.Information("reinstall the previous {Product} (asked by the user)", product.Name);
        _ = _installs.ReinstallPreviousAsync(product);
    }

    public void CancelInstall(ProductInfo product) => _installs?.Cancel(product.Id);

    /// <summary>Opens one of the tray's msiexec logs in Notepad (only a file in the tray's install-log folder).</summary>
    public void OpenLog(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!SelfUpdater.IsUnder(full, TrayPaths.InstallLogsDir) || !File.Exists(full)) return;
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe")) { UseShellExecute = false };
            start.ArgumentList.Add(full);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "could not open the install log {Path}", path);
        }
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public TimeZoneInfo TimeZone => TimeZoneInfo.Local;

    /// <summary>Check now: the window's button and the menu's Check for updates. Never blocks.</summary>
    public void CheckNow()
    {
        if (_quitting || _updates is null) return;
        Log.Information("check for updates now (asked by the user)");
        _ = _updates.CheckNowAsync();
    }

    /// <summary>The product's latest release page on github.com, built from the catalog's repo (never from data).</summary>
    public void OpenReleasePage(ProductInfo product)
    {
        try
        {
            var uri = ReleaseFeed.LatestUri(product.Repo);
            if (!UpdateHosts.IsAllowed(uri, UpdateHostUse.Feed)) return;
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "could not open the release page for {Product}", product.Name);
        }
    }

    /// <summary>Starts the tray: settings, theme, icon, the show signal, then the window (unless --startup) and the notice.</summary>
    public void Start()
    {
        // Windows Installer's Restart Manager (on a repair, upgrade or uninstall that replaces the tray's files)
        // and sign-out send WM_QUERYENDSESSION; WPF's Application then shuts the dispatcher down itself. The tray
        // lets it, and records why. util:CloseApplication's WM_CLOSE (CloseRequested) is the path when Restart
        // Manager is off or did not reach the tray.
        if (Application.Current is { } app) app.SessionEnding += (_, e) => OnSessionEnding(e.ReasonSessionEnding.ToString());

        _settings = TraySettings.Read(log: m => Log.Warning("{Message}", m));
        _checks = UpdateChecksPolicy.Resolve(_settings.CheckForUpdates, WindowsState.UpdateChecksPresets());
        ApplyTheme();
        Log.Information("RST tray {Version} starting (pid {Pid}, {Mode}); theme {Choice} -> {Theme}; update checks {Checks}{Locked}",
            Version, Environment.ProcessId, _args.StartHidden ? "--startup, hidden" : "window", _settings.Theme, RstTheme.Current,
            _checks.Enabled ? "on" : "off", _checks.LockedOff ? " (UpdateChecks preset)" : "");

        _icon = new TrayIcon(TrayAssets.IconFile(), TrayStrings.Tray_Tooltip, m => Log.Warning("{Message}", m));
        _icon.Activated += (_, _) => OpenWindow(null);
        _icon.MenuRequested += (_, point) => OpenMenu(point);
        _icon.NotificationClicked += (_, _) => OpenWindow(TrayPage.Updates);
        _icon.CloseRequested += (_, _) => Quit("WM_CLOSE");
        _icon.SessionEnding += (_, _) => QuitNow("sign-out", insideWindowMessage: true);
        _icon.ThemeChanged += (_, _) => OnWindowsThemeChanged();
        _icon.Resumed += (_, _) => _updates?.Resumed();
        // WU3 review D2: an icon Explorer puts back (TaskbarCreated) also sends a notification that waited for it.
        _icon.Readded += (_, _) =>
        {
            Log.Information("tray icon added again (Explorer restarted)");
            if (_pendingAnnounce is { } pending) Announce(pending);
        };
        ShowIcon();
        AppRestart.Register(m => Log.Warning("{Message}", m));

        // Update checks (F17 WU3): the first automatic slot is 2–10 minutes away; nothing is sent before it, and
        // nothing at all while checks are off. Detection waits for the window or that first check.
        _http = TrayHttp.CreateClient(TrayHttp.CreateHandler());
        _updates = new TrayUpdates(
            ProductCatalog.FromEmbedded(m => Log.Warning("{Message}", m)),
            _http,
            new MsiProducts(m => Log.Warning("{Message}", m)),
            () => ProductDetection.InstalledClientIds(m => Log.Warning("{Message}", m)),
            TrayUpdates.SettingsAndPresetPolicy(settingsPath: null, WindowsState.UpdateChecksPresets),
            _checks.Enabled, // already read above: the pages never read files or the registry on the UI thread
            TimeProvider.System,
            TrayPaths.UpdatesFile,
            action => _dispatcher.BeginInvoke(action),
            m => Log.Information("{Message}", m));
        _updates.NewVersionsFound += (_, found) => Announce(found);
        _updates.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);

        // Installs (F17 WU4): nothing runs until the user clicks Install or Update.
        _revit = new RevitWatch(log: m => Log.Information("{Message}", m));
        _selfUpdater = new SelfUpdater(AppContext.BaseDirectory, TrayPaths.UpdaterDir, Environment.ProcessId, m => Log.Information("{Message}", m));
        _installs = new InstallCoordinator(
            _updates.Current.Products.Select(p => p.Product),
            _http,
            new MsiProducts(m => Log.Warning("{Message}", m)),
            new MsiDatabase(),
            new BundleReader(),
            new Msiexec(),
            _revit,
            _selfUpdater,
            new InstallPaths(TrayPaths.CacheDir, TrayPaths.InstallLogsDir, TrayPaths.SelfUpdateResultFile),
            TimeProvider.System,
            action => _dispatcher.BeginInvoke(action),
            m => Log.Information("{Message}", m),
            quitForSelfUpdate: () => Quit("self-update: the updater installs RST"),
            installsChanged: () => _ = _updates?.RefreshInstalledAsync());
        _installs.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        if (_installs.For(ProductCatalog.RstId).Phase is InstallPhase.Installed or InstallPhase.Failed or InstallPhase.Refused)
            _ = _updates.RefreshInstalledAsync(); // the updater just ran: show what is installed now
        // The updater found Revit open (review B2): this tray waits for Revit and hands over again.
        _ = _installs.ResumeDeferredSelfUpdateAsync();

        _updates.Start();
        if (_updates.Loop.NextDueUtc is { } due)
            Log.Information("first update check slot at {Due:u}{Off}", due, _checks.Enabled ? "" : " (checks are off: it will make no request)");

        _instance.OnShowRequested(() => _dispatcher.BeginInvoke(() =>
        {
            Log.Information("another launch asked for the window");
            _installs?.RefreshSelfUpdateResult(); // the updater's launch, when this tray was restarted by the installer
            OpenWindow(null);
        }));

        if (!_args.StartHidden) OpenWindow(TrayPage.Updates);
        _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            ShowNoticeIfDue();
            ShowShadowNoticeIfDue();
        });
    }

    /// <summary>
    /// Adds the icon; when the shell refuses (Explorer busy at sign-in), retries on one-shot timers per
    /// <see cref="TrayIconRetry"/> (about two minutes), then leaves it to TaskbarCreated.
    /// </summary>
    private void ShowIcon()
    {
        if (_quitting || _icon is null) return;
        if (_icon.Show())
        {
            Log.Information(_iconFailures == 0 ? "tray icon shown" : "tray icon shown after {Retries} retries", _iconFailures);
            _iconFailures = 0;
            if (_pendingAnnounce is { } pending) Announce(pending); // a notification that waited for the icon
            return;
        }
        _iconFailures++;
        if (TrayIconRetry.NextDelay(_iconFailures) is not { } delay)
        {
            Log.Warning("tray icon: not added after {Attempts} attempts; it is added when Explorer next starts", _iconFailures);
            return;
        }
        Log.Information("tray icon: retrying in {Seconds} s", delay.TotalSeconds);
        _iconRetry?.Stop();
        _iconRetry = new DispatcherTimer(delay, DispatcherPriority.Background, (_, _) =>
        {
            _iconRetry?.Stop();
            _iconRetry = null;
            ShowIcon();
        }, _dispatcher);
    }

    /// <summary>Shows the window (building it the first time), on <paramref name="page"/> or the last page, and brings it forward.</summary>
    public void OpenWindow(TrayPage? page)
    {
        if (_quitting) return;
        if (page is { } p) _page = p;
        if (_window is null)
        {
            var built = new TrayWindow(new TrayViewModel(this, _page, this));
            _window = built;
            _ = _updates?.RefreshInstalledAsync(); // what is installed now (local only, no network)
            built.ViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TrayViewModel.Page) && ReferenceEquals(_window, built)) _page = built.ViewModel.Page;
            };
            built.Closed += (_, _) =>
            {
                if (!ReferenceEquals(_window, built)) return;
                _window = null;
                StopRevitPageWatch();
            };
            built.Show();
            WatchRevitForPage();
            Log.Information("window opened ({Page}, {Theme})", _page, RstTheme.Current);
        }
        else
        {
            _window.ViewModel.Page = _page;
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Show();
            _window.ViewModel.Updates.Refresh(); // Revit may have started or closed since
            WatchRevitForPage();
        }
        _window.Activate();
    }

    /// <summary>Ends the tray from the dispatcher (never inside the hidden window's own message handler).</summary>
    public void Quit(string reason) => _dispatcher.BeginInvoke(() => QuitNow(reason));

    public bool SetTheme(TrayThemeChoice choice)
    {
        var saved = TraySettings.Update(s => s.Theme = choice, log: m => Log.Warning("{Message}", m));
        if (saved is null) return false;
        _settings = saved;
        Log.Information("theme set to {Choice}", choice);
        _dispatcher.BeginInvoke(() => ReapplyTheme(force: false));
        return true;
    }

    public bool SetCheckForUpdates(bool on)
    {
        var saved = TraySettings.Update(s => s.CheckForUpdates = on, log: m => Log.Warning("{Message}", m));
        if (saved is null) return false;
        _settings = saved;
        _checks = UpdateChecksPolicy.Resolve(_settings.CheckForUpdates, WindowsState.UpdateChecksPresets());
        Log.Information("check for updates automatically: {On}", on ? "on" : "off");
        _updates?.AutomaticChecksChanged(_checks.Enabled);
        return true;
    }

    public void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { LogFolder }, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "could not open the log folder {Folder}", LogFolder);
        }
    }

    public void Dispose()
    {
        StopUpdates();
        _icon?.Dispose();
        _icon = null;
    }

    /// <summary>
    /// Stops the check loop (an in-flight check is cancelled), any install that is downloading or waiting (an
    /// msiexec already running finishes on its own), and releases the HTTP client. Idempotent.
    /// </summary>
    private void StopUpdates()
    {
        StopRevitPageWatch();
        try
        {
            _installs?.Dispose();
            _updates?.Dispose();
            _http?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "stopping update checks");
        }
        _installs = null;
        _updates = null;
        _http = null;
    }

    /// <summary>
    /// While the window is open and Revit runs, the buttons read "… when Revit closes"; this waits for Revit's exit
    /// (its process events, no polling) and refreshes the pages once, so they say Install and Update again.
    /// </summary>
    private void WatchRevitForPage()
    {
        if (_revit is not { } revit || _revitPageWatch is not null || !RevitRunning) return;
        var cts = new CancellationTokenSource();
        _revitPageWatch = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await revit.WaitUntilNoneAsync(cts.Token).ConfigureAwait(false);
                _ = _dispatcher.BeginInvoke(() =>
                {
                    if (ReferenceEquals(_revitPageWatch, cts)) _revitPageWatch = null;
                    if (!_quitting) Changed?.Invoke(this, EventArgs.Empty);
                });
            }
            catch (OperationCanceledException)
            {
                // The window closed.
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "watching Revit for the pages");
            }
        });
    }

    private void StopRevitPageWatch()
    {
        var cts = _revitPageWatch;
        _revitPageWatch = null;
        if (cts is null) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// One notification for the versions a check found for the first time ("RST 3.1.1 is available"). They are
    /// recorded as announced only once the notification is shown; while the icon is not in the notification area
    /// (Explorer still starting) they wait in <see cref="_pendingAnnounce"/> and go out when the icon is added.
    /// </summary>
    private void Announce(IReadOnlyList<ProductStatus> found)
    {
        if (_quitting || _icon is null || _updates is null || found.Count == 0) return;
        if (!_icon.IsShown)
        {
            _pendingAnnounce = found;
            Log.Information("notification waits for the tray icon: {Versions}", string.Join(", ", found.Select(s => s.Product.Name + " " + s.Latest)));
            return;
        }
        _pendingAnnounce = null;
        string title, text;
        if (found.Count == 1)
        {
            title = TrayStrings.Notify_OneTitle(found[0].Product.Name, found[0].Latest!.Value.ToString());
            text = TrayStrings.Notify_OneText;
        }
        else
        {
            title = TrayStrings.Notify_ManyTitle;
            text = TrayStrings.Notify_ManyText(string.Join(", ", found.Select(s => s.Product.Name + " " + s.Latest!.Value)));
        }
        var shown = _icon.ShowNotification(title, text);
        Log.Information("notification {Shown}: {Title}: {Text}", shown ? "shown" : "refused by the shell (offered again at the next check)", title, text);
        if (shown) _updates.MarkAnnounced(found);
    }

    /// <summary>
    /// Removes the icon, closes the windows and stops the dispatcher, so Application.Run returns 0. On the Quit and
    /// WM_CLOSE paths this runs from the dispatcher queue, so the hidden window is released here too; at sign-out it
    /// runs inside WM_ENDSESSION, where the window is left to Dispose after Run returns.
    /// </summary>
    private void QuitNow(string reason, bool insideWindowMessage = false)
    {
        if (_quitting) return;
        _quitting = true;
        _iconRetry?.Stop();
        ExitReason = reason;
        Log.Information("quitting ({Reason})", reason);
        StopUpdates();
        try
        {
            _icon?.Hide();
            foreach (var window in Application.Current?.Windows.OfType<Window>().ToList() ?? [])
                window.Close();
            if (!insideWindowMessage)
            {
                _icon?.Dispose();
                _icon = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "closing windows on quit");
        }
        Application.Current?.Shutdown(0);
    }

    /// <summary>WPF ends the dispatcher after this returns; the icon goes now, the hidden window at Dispose.</summary>
    private void OnSessionEnding(string reason)
    {
        if (_quitting) return;
        _quitting = true;
        ExitReason = "session ending (" + reason + "): Restart Manager or sign-out";
        Log.Information("Windows asked the tray to close: {Reason}", ExitReason);
        StopUpdates();
        try
        {
            _icon?.Hide();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "removing the icon at session end");
        }
    }

    private void OpenMenu(Point screenPixels)
    {
        if (_quitting || _icon is null) return;
        var menu = TrayMenu.Build(new TrayMenuActions(
            Open: () => OpenWindow(TrayPage.Updates),
            CheckForUpdates: () =>
            {
                CheckNow();
                OpenWindow(TrayPage.Updates);
            },
            Settings: () => OpenWindow(TrayPage.Settings),
            Quit: () => Quit("menu")));
        TrayMenu.Open(menu, screenPixels, _icon.Source);
    }

    private void OnWindowsThemeChanged()
    {
        Log.Debug("Windows theme or contrast changed");
        ReapplyTheme(force: false);
    }

    /// <summary>Resolves the theme again; when it changed and the window is open, the window is rebuilt where it was.</summary>
    private void ReapplyTheme(bool force)
    {
        var before = RstTheme.Current;
        ApplyTheme();
        if ((!force && before == RstTheme.Current) || _window is not { } old) return;

        var bounds = old.WindowState == WindowState.Normal ? new Rect(old.Left, old.Top, old.Width, old.Height) : old.RestoreBounds;
        var maximized = old.WindowState == WindowState.Maximized;
        _window = null;
        old.Close();
        OpenWindow(null);
        if (_window is { } rebuilt && !bounds.IsEmpty)
        {
            rebuilt.WindowStartupLocation = WindowStartupLocation.Manual;
            rebuilt.Left = bounds.Left;
            rebuilt.Top = bounds.Top;
            rebuilt.Width = bounds.Width;
            rebuilt.Height = bounds.Height;
            if (maximized) rebuilt.WindowState = WindowState.Maximized;
        }
        Log.Information("window re-themed ({Theme})", RstTheme.Current);
    }

    private void ApplyTheme() =>
        RstTheme.Apply(TrayTheme.Resolve(_settings.Theme, WindowsState.AppsUseLightTheme(), WindowsState.HighContrast));

    /// <summary>The one-time update-check notice (the telemetry ConsentNotice precedent). Never throws.</summary>
    private void ShowNoticeIfDue()
    {
        try
        {
            if (_quitting || !TrayUpdateNotice.ShouldShow(_settings, _checks)) return;
            var dialog = new MessageDialog(
                new MessageDialogModel(TrayStrings.Notice_Title, TrayStrings.Notice_Heading, TrayStrings.Notice_Message, []),
                NoticeAutomationId)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true,
                Icon = TrayWindow.WindowIcon(),
            };
            if (_window is { IsVisible: true } owner) dialog.Owner = owner;
            Log.Information("showing the update-check notice");
            dialog.ShowDialog();
            if (TrayUpdateNotice.MarkShown(DateTimeOffset.UtcNow, log: m => Log.Warning("{Message}", m)))
                _settings = TraySettings.Read(log: m => Log.Warning("{Message}", m));
            else
                Log.Warning("update-check notice shown but not recorded; it shows again next start");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "update-check notice failed");
        }
    }

    /// <summary>
    /// F17 WU5 (spec #20 §7): when RST is installed both for this user and for everyone on the PC, Revit loads the
    /// user's own copy. Logged at every start; the user is told once. Information only. Never throws.
    /// </summary>
    private void ShowShadowNoticeIfDue()
    {
        try
        {
            if (_quitting) return;
            var shadows = InstallLayout.FindShadows();
            foreach (var shadow in shadows) Log.Information("{Shadow}", InstallLayout.Describe(shadow));
            if (!TrayShadowNotice.ShouldShow(_settings, shadows.Count)) return;
            var dialog = new MessageDialog(
                new MessageDialogModel(TrayStrings.Notice_Title, TrayStrings.Shadow_Heading, TrayStrings.Shadow_Message, []),
                ShadowNoticeAutomationId)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true,
                Icon = TrayWindow.WindowIcon(),
            };
            if (_window is { IsVisible: true } owner) dialog.Owner = owner;
            Log.Information("showing the installed-twice notice");
            dialog.ShowDialog();
            if (TrayShadowNotice.MarkShown(DateTimeOffset.UtcNow, log: m => Log.Warning("{Message}", m)))
                _settings = TraySettings.Read(log: m => Log.Warning("{Message}", m));
            else
                Log.Warning("installed-twice notice shown but not recorded; it shows again next start");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "installed-twice notice failed");
        }
    }

    /// <summary>The tray's version: its informational version without the source-revision suffix.</summary>
    private static string CurrentVersion()
    {
        var info = typeof(TrayHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info)) return typeof(TrayHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
}
