// rst-c's own (not vendored): the RST tray's coordinator (spec #20 §1; F17 WU1). It owns the icon, the one window
// (built lazily on first open, rebuilt on a theme change, released when closed), the settings and the one-time
// "installed twice" notice, and it ends the process cleanly on Quit, on WM_CLOSE (Windows Installer's
// util:CloseApplication) and at sign-out. Event-driven: no polling. Besides the icon's short add retries at sign-in,
// its timers are the update checker's (F17 WU3, TrayUpdates): 2–10 minutes after start, then daily; off sends nothing;
// and the activity fold worker's (F19 #24 WU3, ActivityFoldWorker): folds the add-in's local session logs into rollups
// on its own below-normal thread, 1–3 minutes after start, then every 15 minutes; it reads and writes local files only.
// Installs (F17 WU4, InstallCoordinator) run only on a click: verified download, advice to close Revit, msiexec for
// a companion, and for RST itself the updater copy (SelfUpdater), after which the tray quits.

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using RST.Core.Configuration;
using RST.Core.Telemetry;
using RST.Core.Telemetry.Rollups;
using RST.Core.Tray;
using RST.Core.Updates;
using RST.Tray.AddIns;
using RST.Tray.Resources;
using RST.Tray.Shell;
using RST.Tray.Thumbnails;
using RST.Tray.Updates;
using RST.Tray.Windows;
using RST.UI.Dialogs;
using RST.UI.Health;
using RST.UI.Loader;
using RST.UI.Theming;
using RST.Core.Logging;

namespace RST.Tray;

internal sealed class TrayHost : ITraySettingsHost, ITrayUpdatesHost, IDisposable
{
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
    private SelfUpdater? _selfUpdater;
    private ActivityFoldWorker? _fold;
    private IReadOnlyList<ProductStatus>? _pendingAnnounce;
    private TrayWindow? _window;
    private TrayPage _page = TrayPage.AddIns;
    private bool _quitting;
    private int _iconFailures;
    private DispatcherTimer? _iconRetry;
    private readonly ITrayAddinsBackend _addins;
    private readonly AddinsWork _addinsWork = new();

    public TrayHost(TrayArguments args, SingleInstance instance, ITrayAddinsBackend? addins = null)
    {
        _args = args ?? throw new ArgumentNullException(nameof(args));
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
        _dispatcher = Dispatcher.CurrentDispatcher;
        _addins = addins ?? new TrayAddinsBackend();
    }

    public TrayThemeChoice ThemeChoice => _settings.Theme;

    public UpdateChecksState UpdateChecks => _checks;

    /// <summary>The tray's informational version: the release version and its commit (<c>3.2.0+abc1234</c>).</summary>
    private static readonly string BuildText = BuildVersion.Informational(typeof(TrayHost).Assembly);

    public string Version { get; } = CurrentVersion();

    public string LogFolder => TrayPaths.LogsDir;

    /// <summary>The activity fold worker (spec #24 Tray Worker): the Health page asks it for a pass and listens for
    /// the end of one. Null when it did not start.</summary>
    internal IActivityFoldService? ActivityFold => _fold;

    /// <summary>Why the tray ended, for the exit log line.</summary>
    public string ExitReason { get; private set; } = "dispatcher stopped";

    // ITrayUpdatesHost (F17 WU3): the Updates and Apps pages read the checker through the host.

    public UpdateSnapshot Current => _updates?.Current ?? UpdateSnapshot.Empty(ProductCatalog.Default, _checks.Enabled);

    /// <summary>The checker's snapshot or an install changed (on the UI thread).</summary>
    public event EventHandler? Changed;

    // F17 WU4: installs.

    public IReadOnlyDictionary<string, InstallProgress> Installs => _installs?.Current ?? ITrayUpdatesHost.EmptyInstalls;


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
        _installs.InstallAsync(product, latest).Forget("tray: install " + product.Name);
    }

    public void ReinstallPrevious(ProductInfo product)
    {
        if (_quitting || _installs is null) return;
        Log.Information("reinstall the previous {Product} (asked by the user)", product.Name);
        _installs.ReinstallPreviousAsync(product).Forget("tray: reinstall the previous " + product.Name);
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
        _updates.CheckNowAsync().Forget("tray: check for updates now");
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

    /// <summary>
    /// Starts the tray (SC-046, T2): the icon and the show signal first, so a tray that runs can always be seen and
    /// found by a second launch; then the settings and theme, the update checker, installs, and the window (unless
    /// --startup) and the notices. A later step that fails is logged and the tray runs without that feature. When
    /// the icon cannot be made at all the tray ends, with <see cref="TrayStartup.FatalExitCode"/>, and its
    /// single-instance mutex is released as Main returns, rather than running on with no icon.
    /// </summary>
    public void Start()
    {
        // SC-054: the build (release version and commit) and the runtime ride on the start line. Its head,
        // "RST tray <version> starting (pid N,", is what installer/ci-tray-*.ps1 wait for.
        Log.Information("RST tray {Version} starting (pid {Pid}, {Mode}), build {Build}, {Runtime}",
            Version, Environment.ProcessId, _args.StartHidden ? "--startup, hidden" : "window", BuildText, BuildVersion.Runtime());
        if (TrayStartup.Run(StartSteps()) is not { } failed) return;
        ExitReason = "could not start: " + failed;
        _quitting = true;
        StopUpdates();
        StopActivityFold();
        _icon?.Dispose();
        _icon = null;
        Application.Current?.Shutdown(TrayStartup.FatalExitCode);
    }

    /// <summary>The start's steps, in order (a test reads the order).</summary>
    internal IReadOnlyList<TrayStartStep> StartSteps() =>
    [
        new("icon", StartIcon, Essential: true),
        new("show signal", WireShowSignal),
        new("session end", WatchSessionEnd),
        new("settings and theme", ReadSettingsAndTheme),
        new("restart registration", () => AppRestart.Register(m => Log.Warning("{Message}", m))),
        new("update checks", StartUpdateChecks),
        new("installs", StartInstalls),
        new("update loop", StartUpdateLoop),
        new("activity fold", StartActivityFold),
        new("window", () =>
        {
            if (!_args.StartHidden) OpenLaunchWindow();
        }),
        // Decision #65: no update-check notice at first run; docs/tray.md explains the checks and how to turn them off.
        new("notices", () => _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, ShowShadowNoticeIfDue)),
    ];

    private void StartIcon()
    {
        _icon = new TrayIcon(TrayAssets.NotificationIconFile(), TrayStrings.Tray_Tooltip, m => Log.Warning("{Message}", m));
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
    }

    /// <summary>A later launch of the tray asks this one to show its window.</summary>
    private void WireShowSignal() =>
        _instance.OnShowRequested(() => _dispatcher.BeginInvoke(() =>
        {
            Log.Information("another launch asked for the window");
            OpenLaunchWindow();
        }));

    // Windows Installer's Restart Manager (on a repair, upgrade or uninstall that replaces the tray's files) and
    // sign-out send WM_QUERYENDSESSION; WPF's Application then shuts the dispatcher down itself. The tray lets it,
    // and records why. util:CloseApplication's WM_CLOSE (CloseRequested) is the path when Restart Manager is off or
    // did not reach the tray.
    private void WatchSessionEnd()
    {
        if (Application.Current is { } app) app.SessionEnding += (_, e) => OnSessionEnding(e.ReasonSessionEnding.ToString());
    }

    private void ReadSettingsAndTheme()
    {
        _settings = TraySettings.Read(log: m => Log.Warning("{Message}", m));
        _checks = UpdateChecksPolicy.Resolve(_settings.CheckForUpdates, WindowsState.UpdateChecksPresets());
        ApplyTheme();
        Log.Information("theme {Choice} -> {Theme}; update checks {Checks}{Locked}", _settings.Theme, RstTheme.Current,
            _checks.Enabled ? "on" : "off", _checks.LockedOff ? " (UpdateChecks preset)" : "");
    }

    // Update checks (F17 WU3): the first automatic slot is 2–10 minutes away; nothing is sent before it, and nothing
    // at all while checks are off. Detection waits for the window or that first check.
    private void StartUpdateChecks()
    {
        _http = TrayHttp.CreateClient(TrayHttp.CreateHandler());
        AttachUpdateChecks(new TrayUpdates(
            ProductCatalog.FromEmbedded(m => Log.Warning("{Message}", m)),
            _http,
            new MsiProducts(m => Log.Warning("{Message}", m)),
            () => ProductDetection.InstalledClientIds(m => Log.Warning("{Message}", m)),
            TrayUpdates.SettingsAndPresetPolicy(settingsPath: null, WindowsState.UpdateChecksPresets),
            _checks.Enabled, // already read above: the pages never read files or the registry on the UI thread
            TimeProvider.System,
            TrayPaths.UpdatesFile,
            action => _dispatcher.BeginInvoke(action),
            m => Log.Information("{Message}", m)));
    }

    /// <summary>Owns the checker and wires the production UI notifications; also used by host tests.</summary>
    internal void AttachUpdateChecks(TrayUpdates updates)
    {
        _updates = updates;
        _updates.NewVersionsFound += (_, found) => Announce(found);
        _updates.Changed += (_, _) =>
        {
            Changed?.Invoke(this, EventArgs.Empty);
            PresentSelfUpdateResult();
        };
    }

    // Installs (F17 WU4): nothing runs until the user clicks Install or Update.
    private void StartInstalls()
    {
        if (_updates is null) throw new InvalidOperationException("the update checker did not start");
        _selfUpdater = new SelfUpdater(AppContext.BaseDirectory, TrayPaths.UpdaterDir, Environment.ProcessId, m => Log.Information("{Message}", m));
        AttachInstalls(new InstallCoordinator(
            _updates.Current.Products.Select(p => p.Product),
            _http ?? throw new InvalidOperationException("the update checker has no HTTP client"),
            new MsiProducts(m => Log.Warning("{Message}", m)),
            new MsiDatabase(),
            new BundleReader(),
            new Msiexec(),
            _selfUpdater,
            new InstallPaths(TrayPaths.CacheDir, TrayPaths.InstallLogsDir, TrayPaths.SelfUpdateResultFile),
            TimeProvider.System,
            action => _dispatcher.BeginInvoke(action),
            m => Log.Information("{Message}", m),
            quitForSelfUpdate: () => Quit("self-update: the updater installs RST"),
            installsChanged: () => _updates?.RefreshInstalledAsync().Forget("tray: read what is installed")));
        if (_installs is null) throw new InvalidOperationException("the install coordinator did not start");
        if (_installs.For(ProductCatalog.RstId).Phase is InstallPhase.Installed or InstallPhase.Failed or InstallPhase.Refused)
            _updates.RefreshInstalledAsync().Forget("tray: read what is installed"); // the updater just ran: show what is installed now
    }

    /// <summary>Owns the coordinator and wires the production UI notifications; also used by host tests.</summary>
    internal void AttachInstalls(InstallCoordinator installs)
    {
        _installs = installs;
        _installs.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    private void StartUpdateLoop()
    {
        if (_updates is null) throw new InvalidOperationException("the update checker did not start");
        _updates.Start();
        if (_updates.Loop.NextDueUtc is { } due)
            Log.Information("first update check slot at {Due:u}{Off}", due, _checks.Enabled ? "" : " (checks are off: it will make no request)");
    }

    // The activity fold (spec #24, Tray Worker): the tray is the only writer of rollups. It runs whatever the
    // update-check setting and whether collection is on (files already recorded still fold); it sends nothing.
    private void StartActivityFold()
    {
        var engine = new ActivityFoldEngine(new RollupStoreOptions { Log = m => Log.Warning("{Message}", m) });
        _fold = ActivityFoldWorker.ForEngine(
            engine,
            backstopPrune: () => RetentionPruner.Prune(
                AppDataPaths.TelemetryOutboxDir,
                TelemetryPrefs.Read().RetentionDays,
                DateTimeOffset.UtcNow,
                m => Log.Warning("{Message}", m)).Count,
            TimeProvider.System,
            info: m => Log.Information("{Message}", m),
            warn: m => Log.Warning("{Message}", m));
        _fold.Start();
    }

    /// <summary>Stops the fold worker: a running pass stops after its current step; waits at most 2 s. Idempotent.</summary>
    private void StopActivityFold()
    {
        var fold = _fold;
        _fold = null;
        try
        {
            fold?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "stopping the activity fold");
        }
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

    private void OpenLaunchWindow()
    {
        // Restart Manager may already have consumed the result while hidden, or it may have arrived since startup.
        _installs?.RefreshSelfUpdateResult();
        OpenWindow(_installs?.HasUnpresentedSelfUpdateResult == true ? TrayPage.Updates : null);
    }

    private void PresentSelfUpdateResult()
    {
        if (_window is { } window && TrayNavigation.CanPresentSelfUpdateResult(
            window.IsVisible, window.WindowState == WindowState.Minimized,
            window.ViewModel.Page, window.ViewModel.Updates.Rst.HasNote))
            _installs?.MarkSelfUpdateResultPresented();
    }

    /// <summary>Opens on the target or RST Profiles; an untargeted activation keeps a shown window's page.</summary>
    public void OpenWindow(TrayPage? page)
    {
        if (_quitting) return;
        var shown = _window?.IsVisible == true;
        if (!shown) _window?.ViewModel.AddIns.ClearLaunchSuccess();
        _page = TrayNavigation.PageOnOpen(page, shown, _page);
        if (_window is null)
        {
            // The Add-ins page (F17 WU2) shows the Loader's own dialogs (Confirm Add-in Changes, the reports, "Adopt
            // RSTify Presets?"), owned by this window.
            var dialogs = new LoaderDialogs();
            var messages = new DialogService();
            // The Health page's Device sub-tab (spec #25): the add-in's snapshot and cleanup, outside Revit, in
            // tray_health_scan.json; its "Delete selected junk?" is owned by this window too. Its Models sub-tab
            // (spec #25 WU3): the activity index, freshened by asking the fold worker for a pass each time it is
            // shown, and the cards' Windows thumbnails (local files only), cached under %LocalAppData%\RST\Tray.
            // A card opens its detail (WU4): the model read through spec #24's shared read path, off the UI thread, its
            // header thumbnail from the same thumbnails the list uses.
            var thumbnails = ModelThumbnails.ForTray();
            var models = new ModelsViewModel(new ModelsViewModelOptions
            {
                Source = new IndexModelListSource(),
                Fold = _fold,
                Thumbnails = thumbnails,
            });
            var detail = new ModelDetailViewModel(new ModelDetailViewModelOptions
            {
                Source = new RollupModelActivitySource(),
                Thumbnails = thumbnails,
            });
            var health = new TrayHealthViewModel(new DeviceHealthService(), messages, models: models, detail: detail);
            var built = new TrayWindow(new TrayViewModel(this, _page, this, new AddInsViewModel(_addins, dialogs, messages, _addinsWork), health));
            dialogs.Owner = built;
            messages.Owner = built;
            _window = built;
            _updates?.RefreshInstalledAsync().Forget("tray: read what is installed"); // what is installed now (local only, no network)
            built.ViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TrayViewModel.Page) && ReferenceEquals(_window, built))
                {
                    _page = built.ViewModel.Page;
                    PresentSelfUpdateResult();
                }
            };
            built.StateChanged += (_, _) =>
            {
                if (ReferenceEquals(_window, built)) PresentSelfUpdateResult();
            };
            built.Closed += (_, _) =>
            {
                if (!ReferenceEquals(_window, built)) return;
                _window = null;
            };
            built.Show();
            Log.Information("window opened ({Page}, {Theme})", _page, RstTheme.Current);
        }
        else
        {
            _window.ViewModel.Page = _page;
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Show();
            _window.ViewModel.Updates.Refresh();
        }
        _window.Activate();
        PresentSelfUpdateResult();
    }

    /// <summary>
    /// Ends the tray from the dispatcher, never inside the hidden window's own message handler: an open tray menu
    /// (whose loop runs inside that handler and keeps draining the dispatcher) is closed first, and the quit runs
    /// once its loop has returned (SC-043 review N1).
    /// </summary>
    public void Quit(string reason, int exitCode = 0) => _dispatcher.BeginInvoke(() => QuitNow(reason, exitCode: exitCode));

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
        StopActivityFold();
        _icon?.Dispose();
        _icon = null;
    }

    /// <summary>
    /// Stops the check loop (an in-flight check is cancelled), any install that is downloading or waiting (an
    /// msiexec already running finishes on its own), and releases the HTTP client. Idempotent.
    /// </summary>
    private void StopUpdates()
    {
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
    /// Removes the icon, closes the windows and stops the dispatcher, so Application.Run returns <paramref name="exitCode"/> (0 but after a repeating UI fault). On the Quit and
    /// WM_CLOSE paths this runs from the dispatcher queue, so the hidden window is released here too; at sign-out it
    /// runs inside WM_ENDSESSION, where the window is left to Dispose after Run returns.
    /// </summary>
    private void QuitNow(string reason, bool insideWindowMessage = false, int exitCode = 0)
    {
        if (_quitting) return;
        // SC-043 review N1: a quit that arrives while the menu is open closes it and runs again after its loop has
        // returned. Inside WM_ENDSESSION there is no later: the menu is ended and the quit goes on now.
        if (insideWindowMessage) TrayMenu.Close();
        else if (TrayMenu.Close(() => QuitNow(reason, exitCode: exitCode))) return;
        _quitting = true;
        _iconRetry?.Stop();
        ExitReason = reason;
        Log.Information("quitting ({Reason})", reason);
        WaitForAddinWork();
        StopUpdates();
        StopActivityFold();
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
        Application.Current?.Shutdown(exitCode);
    }

    /// <summary>
    /// An Apply or Restore All in flight writes Revit's settings files and then RST's record; it finishes (it takes
    /// milliseconds) before the tray ends, so no add-in is left switched off without a record (review N7).
    /// </summary>
    private void WaitForAddinWork()
    {
        if (_addinsWork.IsIdle) return;
        Log.Information("waiting for add-in changes in flight");
        if (!_addinsWork.WaitIdle(TimeSpan.FromSeconds(5)))
            Log.Warning("add-in changes still running after 5 s; quitting anyway");
    }

    /// <summary>WPF ends the dispatcher after this returns; the icon goes now, the hidden window at Dispose.</summary>
    private void OnSessionEnding(string reason)
    {
        if (_quitting) return;
        TrayMenu.Close(); // review N1: the session is ending now; no menu loop is left running under the shutdown
        _quitting = true;
        ExitReason = "session ending (" + reason + "): Restart Manager or sign-out";
        Log.Information("Windows asked the tray to close: {Reason}", ExitReason);
        WaitForAddinWork();
        StopUpdates();
        StopActivityFold();
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
        // SC-043: the native menu; Open returns once the user picked an item (its action has run) or dismissed it.
        TrayMenu.Open(MenuActions(), screenPixels, _icon.Source);
    }

    internal TrayMenuActions MenuActions() => new(
            Open: () => OpenWindow(null),
            CheckForUpdates: () =>
            {
                CheckNow();
                OpenWindow(TrayPage.Updates);
            },
            Settings: () => OpenWindow(TrayPage.Settings),
            Quit: () => Quit("menu"));

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
        OpenWindow(_page); // A theme rebuild preserves the page; it is not a fresh user open.
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
    private static string CurrentVersion() => BuildVersion.Product(BuildText);
}
