// rst-c's own (not vendored): the RST tray's small decisions (spec #20 §1, §4), kept Revit- and
// Windows-free so RST.Tests covers them on Linux. RST.Tray reads the registry and system state and passes
// the values in.

using System;
using System.Collections.Generic;
using RST.Core.Theming;

namespace RST.Core.Tray;

/// <summary>Which palette the tray's window and menu use.</summary>
public static class TrayTheme
{
    /// <summary>
    /// <paramref name="choice"/> Light or Dark is used as set; System follows the Windows app theme
    /// (<c>AppsUseLightTheme</c> = 0 is dark; missing or unreadable is light, as Windows treats it). High
    /// contrast is always light: the palette is not a high-contrast theme, and gantt-it's rule is "light for high
    /// contrast or anything unreadable".
    /// </summary>
    public static UiTheme Resolve(TrayThemeChoice choice, bool? appsUseLightTheme, bool highContrast)
    {
        if (highContrast) return UiTheme.Light;
        return choice switch
        {
            TrayThemeChoice.Light => UiTheme.Light,
            TrayThemeChoice.Dark => UiTheme.Dark,
            _ => appsUseLightTheme == false ? UiTheme.Dark : UiTheme.Light,
        };
    }
}

/// <summary>Whether the tray checks for updates, and whether the user may change that.</summary>
/// <param name="Enabled">Checks run automatically.</param>
/// <param name="LockedOff">An IT preset turned them off; Settings shows the toggle off and disabled.</param>
public readonly record struct UpdateChecksState(bool Enabled, bool LockedOff);

public static class UpdateChecksPolicy
{
    /// <summary>
    /// <c>UpdateChecks</c> = 0 under <c>HKMU\Software\Designs-OS\RST\Tray</c> forces checks off (spec #20 §4).
    /// The tray reads it under both HKLM and HKCU, because HKMU is HKCU for a per-user install and HKLM for a
    /// per-machine one; a 0 in either wins. Any other value, or none, leaves the user's setting in charge.
    /// </summary>
    public static UpdateChecksState Resolve(bool userSetting, IEnumerable<int?> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        foreach (var preset in presets)
            if (preset == 0) return new UpdateChecksState(Enabled: false, LockedOff: true);
        return new UpdateChecksState(userSetting, LockedOff: false);
    }
}

/// <summary>
/// The one-time update-check notice, following the telemetry ConsentNotice precedent (RST.Engine): shown on the
/// first tray start where checks are on and the notice was never shown; recorded however it is dismissed.
/// </summary>
public static class TrayUpdateNotice
{
    /// <summary>Checks on (not preset off, not switched off) and the notice never shown.</summary>
    public static bool ShouldShow(TraySettings settings, UpdateChecksState checks)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return checks.Enabled && settings.UpdateNoticeShownUtc is null;
    }

    /// <summary>Records the notice as shown, merging into the freshest settings. False if nothing was written.</summary>
    public static bool MarkShown(DateTimeOffset shownUtc, string? path = null, Action<string>? log = null) =>
        TraySettings.Update(s => s.UpdateNoticeShownUtc = shownUtc, path, log) is not null;
}

/// <summary>
/// When to try adding the notification-area icon again after Shell_NotifyIcon(NIM_ADD) fails (spec #20 §1; review
/// N4). At sign-in Explorer is often up but busy: the add times out and no TaskbarCreated follows, so the tray
/// retries a few times, one-shot timers with a growing gap, about two minutes in all, then waits for TaskbarCreated
/// only. Not a polling loop: each retry is scheduled only after a failure.
/// </summary>
public static class TrayIconRetry
{
    /// <summary>The gaps before each retry: 2, 4, 8, 16, 30 and 60 seconds (2 min in all).</summary>
    public static readonly IReadOnlyList<TimeSpan> Delays = new[]
    {
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
    };

    /// <summary>
    /// The wait before retry number <paramref name="failedAttempts"/> (1 after the first failed add), or null when
    /// the retries are used up.
    /// </summary>
    public static TimeSpan? NextDelay(int failedAttempts) =>
        failedAttempts >= 1 && failedAttempts <= Delays.Count ? Delays[failedAttempts - 1] : null;
}

/// <summary>The tray's command line.</summary>
/// <param name="StartHidden"><c>--startup</c> (the Run value at sign-in): start in the notification area, no window.</param>
/// <param name="Unknown">Arguments the tray does not know (logged and ignored).</param>
public sealed record TrayArguments(bool StartHidden, IReadOnlyList<string> Unknown)
{
    public static TrayArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var startHidden = false;
        var unknown = new List<string>();
        foreach (var arg in args)
        {
            if (string.Equals(arg, TrayPaths.StartupArgument, StringComparison.OrdinalIgnoreCase)) startHidden = true;
            else if (!string.IsNullOrWhiteSpace(arg)) unknown.Add(arg);
        }
        return new TrayArguments(startHidden, unknown);
    }
}
