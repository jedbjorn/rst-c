// rst-c's own (not vendored): the RST tray's window and notice rendered off-screen in light and dark (spec #20
// AC11; F17 WU1; the icon's menu is a native Win32 menu since SC-043, tested in TrayMenuTests), plus the view-model's Settings behavior, the strings, the icon loader and the single-instance
// hand-off on a real Windows runtime. Each screenshot test also checks what it shows (the visible page, the
// header color, the toggle's state), so a broken template fails here as well as in review. The PNGs land in
// TestResults/screenshots; CI uploads them as RST-screenshots (R25 leg).
// SC-048 item 2: the notification resource has real small-size entries; the window retains rst.ico.

using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RST.Core.Theming;
using RST.Core.Tray;
using RST.RuntimeTests.Support;
using RST.Tray;
using RST.Tray.Resources;
using RST.Tray.Shell;
using RST.Tray.Windows;
using RST.UI.Dialogs;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.Tray;

/// <summary>A scripted tray host: records what the window asked for.</summary>
internal sealed class FakeTrayHost : ITraySettingsHost
{
    public TrayThemeChoice ThemeChoice { get; set; } = TrayThemeChoice.System;

    public UpdateChecksState UpdateChecks { get; set; } = new(Enabled: true, LockedOff: false);

    public string Version { get; set; } = "3.1.0";

    public string LogFolder { get; set; } = @"C:\Users\modeller\AppData\Roaming\RST\logs";

    public bool SaveSucceeds { get; set; } = true;

    public List<string> Calls { get; } = new();

    public bool SetTheme(TrayThemeChoice choice)
    {
        Calls.Add("theme:" + choice);
        if (SaveSucceeds) ThemeChoice = choice;
        return SaveSucceeds;
    }

    public bool SetCheckForUpdates(bool on)
    {
        Calls.Add("checks:" + on);
        if (SaveSucceeds) UpdateChecks = new UpdateChecksState(on, false);
        return SaveSucceeds;
    }

    public void OpenLogFolder() => Calls.Add("logs");
}

[Collection(WpfThemeCollection.Name)]
public class TrayWindowScreenshotTests
{
    private static readonly Size WindowSize = new(720, 540);

    public static TheoryData<UiTheme> Themes => new() { UiTheme.Light, UiTheme.Dark };

    public static TheoryData<TrayPage, UiTheme> PagesAndThemes()
    {
        var data = new TheoryData<TrayPage, UiTheme>();
        foreach (var page in Enum.GetValues<TrayPage>())
            foreach (var theme in new[] { UiTheme.Light, UiTheme.Dark })
                data.Add(page, theme);
        return data;
    }

    private static string Name(string what, UiTheme theme) => $"tray-{what}-{theme.ToString().ToLowerInvariant()}";

    private static Color Palette(UiTheme theme, string key)
    {
        var c = UiPalette.For(theme)[key];
        return Color.FromRgb(c.R, c.G, c.B);
    }

    private static void AssertColor(Color expected, Color actual, string what)
    {
        var distance = Math.Max(Math.Abs(expected.R - actual.R), Math.Max(Math.Abs(expected.G - actual.G), Math.Abs(expected.B - actual.B)));
        Assert.True(distance <= 3, $"{what}: expected {expected}, rendered {actual}");
    }

    private static void Sta(UiTheme theme, Action action) => Screenshots.RunSta(() =>
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        SynchronizationContext.SetSynchronizationContext(null);
        RstTheme.Apply(theme);
        try
        {
            action();
        }
        finally
        {
            RstTheme.Apply(UiTheme.Light);
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });

    private static BitmapSource Capture(Window window, string name)
    {
        WindowCapture.ShowOffScreen(window);
        WindowCapture.DoEvents();
        var bitmap = WindowCapture.Content(window, WindowSize);
        Screenshots.Save(bitmap, name);
        Assert.True(Screenshots.DistinctPixels(bitmap) > 40, $"{name} looks blank");
        return bitmap;
    }

    private static string PageName(TrayPage page) => page switch
    {
        TrayPage.AddIns => "addins",
        _ => page.ToString().ToLowerInvariant(),
    };

    [Theory]
    [MemberData(nameof(PagesAndThemes))]
    public void Each_page_renders_in_the_family_look(TrayPage page, UiTheme theme) => Sta(theme, () =>
    {
        var window = new TrayWindow(new TrayViewModel(new FakeTrayHost(), page));
        try
        {
            var bitmap = Capture(window, Name(PageName(page), theme));

            // Exactly the chosen page is visible; the tab strip agrees.
            var pages = new Dictionary<TrayPage, FrameworkElement>
            {
                [TrayPage.Updates] = window.UpdatesPage,
                [TrayPage.Apps] = window.AppsPage,
                [TrayPage.AddIns] = window.AddInsPage,
                [TrayPage.Health] = window.HealthPage,
                [TrayPage.Settings] = window.SettingsPage,
            };
            foreach (var (p, element) in pages)
                Assert.Equal(p == page ? Visibility.Visible : Visibility.Collapsed, element.Visibility);
            Assert.Equal((int)page, window.Tabs.SelectedIndex);

            // Header and footer in the surface color, the page in the window color (the family palette).
            AssertColor(Palette(theme, UiPalette.Surface), Screenshots.PixelAt(bitmap, 2, 2), "header");
            AssertColor(Palette(theme, UiPalette.Surface), Screenshots.PixelAt(bitmap, 2, bitmap.PixelHeight - 3), "footer");
            AssertColor(Palette(theme, UiPalette.Window), Screenshots.PixelAt(bitmap, 4, bitmap.PixelHeight / 2), "page");
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void Settings_locked_by_the_UpdateChecks_preset(UiTheme theme) => Sta(theme, () =>
    {
        var host = new FakeTrayHost { UpdateChecks = new UpdateChecksState(Enabled: false, LockedOff: true), ThemeChoice = TrayThemeChoice.Dark };
        var window = new TrayWindow(new TrayViewModel(host, TrayPage.Settings));
        try
        {
            Capture(window, Name("settings-locked", theme));
            Assert.False(window.CheckForUpdatesToggle.IsChecked);
            Assert.False(window.CheckForUpdatesToggle.IsEnabled);
            Assert.True(window.ThemeDark.IsChecked);
        }
        finally
        {
            window.Close();
        }
    });

    // F17 WU5 (spec #20 §7): the one-time "installed twice" notice, in the same dialog and family look.
    [Theory]
    [MemberData(nameof(Themes))]
    public void The_installed_twice_notice(UiTheme theme) => Sta(theme, () =>
    {
        var dialog = new MessageDialog(
            new MessageDialogModel(TrayStrings.Notice_Title, TrayStrings.Shadow_Heading, TrayStrings.Shadow_Message, []),
            TrayHost.ShadowNoticeAutomationId);
        try
        {
            WindowCapture.ShowOffScreen(dialog);
            WindowCapture.DoEvents();
            var bitmap = WindowCapture.Client(dialog);
            Screenshots.Save(bitmap, Name("shadow-notice", theme));
            AssertColor(Palette(theme, UiPalette.Window), Screenshots.PixelAt(bitmap, 1, 1), "notice background");
            Assert.True(Screenshots.DistinctPixels(bitmap) > 30, "the notice looks blank");
        }
        finally
        {
            dialog.Close();
        }
    });
}

public class TrayViewModelTests
{
    [Fact]
    public void Theme_pills_save_through_the_host_and_report_a_failed_save()
    {
        var host = new FakeTrayHost();
        var vm = new TrayViewModel(host, TrayPage.Settings);
        Assert.True(vm.ThemeSystem);

        vm.ThemeDark = true;
        Assert.Equal(new[] { "theme:Dark" }, host.Calls);
        Assert.True(vm.ThemeDark);
        Assert.False(vm.ThemeSystem);
        Assert.False(vm.HasSaveError);

        vm.ThemeSystem = false; // a radio button unchecking: nothing to do
        Assert.Single(host.Calls);

        host.SaveSucceeds = false;
        vm.ThemeLight = true;
        Assert.True(vm.ThemeDark, "a failed save keeps the saved theme");
        Assert.True(vm.HasSaveError);
        Assert.Equal(TrayStrings.Settings_SaveFailed, vm.SaveError);
    }

    [Fact]
    public void Check_for_updates_saves_and_the_preset_locks_it_off()
    {
        var host = new FakeTrayHost();
        var vm = new TrayViewModel(host, TrayPage.Settings);
        Assert.True(vm.CheckForUpdates);
        Assert.True(vm.CanChangeCheckForUpdates);
        vm.CheckForUpdates = false;
        Assert.Equal(new[] { "checks:False" }, host.Calls);
        Assert.False(vm.CheckForUpdates);

        var locked = new FakeTrayHost { UpdateChecks = new UpdateChecksState(false, true) };
        var lockedVm = new TrayViewModel(locked, TrayPage.Settings);
        Assert.False(lockedVm.CheckForUpdates);
        Assert.True(lockedVm.IsCheckForUpdatesLocked);
        lockedVm.CheckForUpdates = true;
        Assert.Empty(locked.Calls);
        Assert.False(lockedVm.CheckForUpdates);
    }

    [Fact]
    public void Pages_version_and_log_folder()
    {
        var host = new FakeTrayHost();
        var vm = new TrayViewModel(host, TrayPage.Updates);
        Assert.True(vm.IsUpdatesPage);
        vm.PageIndex = 3;
        Assert.Equal(TrayPage.Health, vm.Page); // spec #25: Health sits between Add-ins and Settings
        Assert.True(vm.IsHealthPage);
        vm.PageIndex = 4;
        Assert.Equal(TrayPage.Settings, vm.Page);
        Assert.True(vm.IsSettingsPage);
        vm.PageIndex = 9;
        Assert.Equal(TrayPage.Settings, vm.Page);
        Assert.Equal("RST 3.1.0", vm.VersionText);
        vm.OpenLogFolderCommand.Execute(null);
        Assert.Equal(new[] { "logs" }, host.Calls);
    }
}

public class TrayShellTests
{
    [Fact]
    public void Every_TrayStrings_accessor_resolves_to_a_resx_entry()
    {
        var count = 0;
        foreach (var p in typeof(TrayStrings).GetProperties(BindingFlags.Static | BindingFlags.Public))
        {
            var value = (string)p.GetValue(null)!;
            Assert.False(string.IsNullOrWhiteSpace(value), p.Name);
            Assert.NotEqual(p.Name, value);
            count++;
        }
        Assert.Equal("RST 3.1.0", TrayStrings.Settings_VersionFormat("3.1.0"));
        Assert.True(count >= 35, $"only {count} accessors");
    }

    [Fact]
    public void The_embedded_icon_loads_at_the_small_icon_size()
    {
        var ico = TrayAssets.NotificationIconFile();
        Assert.NotEmpty(ico);
        Assert.NotEqual(TrayAssets.IconFile(), ico);
        int[] sizes = [16, 20, 24, 32, 40, 48];
        Assert.Equal(sizes.Length, BitConverter.ToUInt16(ico, 4));
        for (var i = 0; i < sizes.Length; i++)
        {
            Assert.Equal(sizes[i], ico[6 + i * 16]);
            Assert.Equal(sizes[i], ico[7 + i * 16]);
            var icon = TrayIcon.LoadIcon(ico, sizes[i], sizes[i]);
            Assert.NotEqual(IntPtr.Zero, icon);
            DestroyIcon(icon);
        }
        Assert.NotEmpty(TrayAssets.IconFile());
        Assert.Equal(IntPtr.Zero, TrayIcon.LoadIcon(new byte[] { 0, 0, 2, 0, 0, 0 }, 16, 16));
    }

    [Fact]
    public void A_second_claim_is_refused_and_signals_the_first()
    {
        var sid = "S-1-5-21-424242-" + Environment.ProcessId + "-" + Random.Shared.Next(100000, 999999);
        var log = new List<string>();
        using var first = SingleInstance.TryClaim(sid, signalFirst: true, log.Add);
        Assert.NotNull(first);
        using var shown = new ManualResetEventSlim();
        first!.OnShowRequested(shown.Set);

        // The mutex is re-entrant on its owning thread: the second launch runs on another one.
        var second = OnOtherThread(() => SingleInstance.TryClaim(sid, signalFirst: true, log.Add));
        Assert.Null(second);
        Assert.True(shown.Wait(TimeSpan.FromSeconds(10)), "the first instance was not asked to show its window");

        shown.Reset();
        var quiet = OnOtherThread(() => SingleInstance.TryClaim(sid, signalFirst: false, log.Add));
        Assert.Null(quiet);
        Assert.False(shown.Wait(TimeSpan.FromMilliseconds(500)), "a --startup launch must not open the window");
    }

    [Fact]
    public void Restart_registration_asks_for_a_quiet_restart_after_updates_only()
    {
        Assert.Equal("--startup", AppRestart.CommandLine);
        Assert.Equal(0x1 | 0x2 | 0x8, AppRestart.Flags); // no crash, no hang, no reboot restarts; patch restarts allowed
        var log = new List<string>();
        try
        {
            AppRestart.Register(log.Add);
            Assert.Empty(log);
            var buffer = new char[260];
            var size = (uint)buffer.Length;
            Assert.Equal(0, GetApplicationRestartSettings(GetCurrentProcess(), buffer, ref size, out var flags));
            Assert.Equal("--startup", new string(buffer, 0, (int)size).TrimEnd('\0'));
            Assert.Equal((uint)AppRestart.Flags, flags);
        }
        finally
        {
            UnregisterApplicationRestart();
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetApplicationRestartSettings(IntPtr process, char[] commandLine, ref uint size, out uint flags);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int UnregisterApplicationRestart();

    private static T OnOtherThread<T>(Func<T> work)
    {
        T result = default!;
        var thread = new Thread(() => result = work());
        thread.Start();
        thread.Join();
        return result;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
