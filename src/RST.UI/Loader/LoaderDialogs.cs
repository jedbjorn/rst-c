// rst-c's own (not vendored): the Profile Loader's two own dialogs (spec #16, WU3), behind a seam
// the view-model tests fake. Spec #18 (feature #16, In Scope 5) rebuilt both on the add-in plan and
// report:
//   - "Confirm Add-in Changes" shows the plan the Load will run, per Revit year and de-duplicated
//     ("RST-Probe (2026, 2027)"): switched off, switched back on, staying on (required), and what is
//     already known not to change (an admin lock, an unreadable settings file). NotManaged rows are
//     left out and "Try Disable" is gone.
//   - One report window for Load Profile, Unload, Restore All Add-ins and the one-time move of
//     v3.0's renamed manifests: what changed, what could not (with the reason, per year), the
//     required add-ins that are missing or switched off, and always "Add-in changes apply the next
//     time Revit starts". Nothing is silent.

using System.Diagnostics;
using System.Windows;
using RST.Core.Activation;
using RST.Core.Loader;
using RST.UI.Mvvm;
using RST.UI.Resources;
using RST.Core.Logging;

namespace RST.UI.Loader;

/// <summary>The Loader's own modal dialogs.</summary>
public interface ILoaderDialogs
{
    /// <summary>"Confirm Add-in Changes": true for Confirm &amp; Load, false for Cancel.</summary>
    bool ConfirmAddinChanges(ConfirmAddinChangesModel model);

    /// <summary>The add-in report; returns when the user clicks Continue.</summary>
    void ShowAddinReport(AddinReportModel model);
}

/// <summary>Opens the Loader's dialogs, owned by the Loader window.</summary>
public sealed class LoaderDialogs : ILoaderDialogs
{
    public Window? Owner { get; set; }

    public bool ConfirmAddinChanges(ConfirmAddinChangesModel model)
    {
        var dialog = new ConfirmAddinChangesWindow(model) { Owner = Owner };
        return dialog.ShowDialog() == true;
    }

    public void ShowAddinReport(AddinReportModel model)
    {
        var dialog = new AddinReportWindow(model) { Owner = Owner };
        dialog.ShowDialog();
    }
}

/// <summary>What "Confirm Add-in Changes" lists, from the plan the load will run.</summary>
public sealed class ConfirmAddinChangesModel
{
    public ConfirmAddinChangesModel(
        IEnumerable<string> switchingOff,
        IEnumerable<string> switchingOn,
        IEnumerable<string> staying,
        IEnumerable<string> cannotChange,
        IReadOnlyList<int>? years = null)
    {
        SwitchingOff = switchingOff.ToList();
        SwitchingOn = switchingOn.ToList();
        Staying = staying.ToList();
        CannotChange = cannotChange.ToList();
        Subtitle = UiStrings.Loader_ConfirmSubtitle(ActivationWording.Years(years ?? []));
    }

    /// <summary>The plan, per year and de-duplicated by add-in name, without its NotManaged rows.</summary>
    public static ConfirmAddinChangesModel From(ActivationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var changes = plan.Changes;
        // "RST-Probe (2026, 2027)": one line per add-in, with the years it applies to.
        IReadOnlyList<string> Grouped(PlanAction action) =>
            ActivationWording.GroupByName(changes.Where(e => e.Action == action && e.ExpectedBlock is null), e => e.Name, e => e.Year);
        var staying = ActivationWording.GroupByName(
            plan.Entries.Where(e => e.Required && e.Action == PlanAction.NoChange), e => e.Name, e => e.Year);
        var blocked = changes.Where(e => e.ExpectedBlock is not null).Select(ActivationWording.BlockedLine).Distinct();
        return new ConfirmAddinChangesModel(Grouped(PlanAction.Disable), Grouped(PlanAction.Enable), staying, blocked, plan.Years);
    }

    public string Subtitle { get; }

    public IReadOnlyList<string> SwitchingOff { get; }

    public IReadOnlyList<string> SwitchingOn { get; }

    public IReadOnlyList<string> Staying { get; }

    public IReadOnlyList<string> CannotChange { get; }

    public bool HasSwitchingOff => SwitchingOff.Count > 0;

    public bool HasSwitchingOn => SwitchingOn.Count > 0;

    public bool HasStaying => Staying.Count > 0;

    /// <summary>The "Can't Be Changed" panel shows only when it has entries.</summary>
    public bool HasCannotChange => CannotChange.Count > 0;

    public string NextStartNote => UiStrings.Loader_ConfirmRestart;
}

/// <summary>One "Missing — Not Installed" line of the report.</summary>
public sealed record AddinCheckLine(string Text, string? Url)
{
    /// <summary>"download ↗" shows only for a link that opens (http/https); otherwise "(no link)".</summary>
    public bool HasUrl => ExternalLinks.IsOpenable(Url);
}

/// <summary>The add-in report after Load Profile, Unload, Restore All Add-ins or the v3.0 move.</summary>
public sealed class AddinReportModel
{
    public AddinReportModel(string title, string subtitle, ActivationReport report, QaCheck? qa = null, Action<string>? openUrl = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        Title = title;
        Subtitle = subtitle;
        var done = report.Done;
        SwitchedOff = ActivationWording.GroupByName(done.Where(o => o.Action == PlanAction.Disable && !o.Migrated), o => o.Name, o => o.Year);
        var on = done.Where(o => o.Action == PlanAction.Enable).ToList();
        SwitchedOn =
        [
            .. ActivationWording.GroupByName(on.Where(o => !o.Required), o => o.Name, o => o.Year),
            .. ActivationWording.GroupByName(on.Where(o => o.Required), o => o.Name, o => o.Year).Select(UiStrings.Loader_ReportRequired),
        ];
        Moved = ActivationWording.GroupByName(done.Where(o => o.Migrated), o => o.Name, o => o.Year);
        NotChanged = report.NotChanged.Select(ActivationWording.NotChangedLine).ToList();
        Notes = report.Notes;
        var check = qa ?? QaCheck.Clear;
        Missing = check.NotInstalled.Select(n =>
            new AddinCheckLine(FirstNonEmpty(n.TabName, n.AddinFile) ?? UiStrings.Loader_QaUnknown, string.IsNullOrEmpty(n.Url) ? null : n.Url)).ToList();
        RequiredOff = check.RequiredOff;
        var open = openUrl ?? ExternalLinks.Open;
        OpenUrlCommand = new RelayCommand<string>(url => open(url));
    }

    /// <summary>The report after Load Profile: the "Add-in Check" with the required-add-in status.</summary>
    public static AddinReportModel ForLoad(string profileName, ActivationReport report, QaCheck qa, Action<string>? openUrl = null)
    {
        var clear = qa.AllClear && report.Outcomes.Count == 0 && report.Notes.Count == 0;
        return new AddinReportModel(UiStrings.Loader_QaTitle, clear ? UiStrings.Loader_QaAllClear : UiStrings.Loader_QaReview(profileName), report, qa, openUrl)
        {
            NothingText = UiStrings.Loader_QaNothing,
        };
    }

    public static AddinReportModel ForUnload(ActivationReport report) =>
        new(UiStrings.Loader_UnloadReportTitle, UiStrings.Loader_UnloadReportSubtitle, report);

    public static AddinReportModel ForRestoreAll(ActivationReport report) =>
        new(UiStrings.Loader_RestoreReportTitle, UiStrings.Loader_RestoreReportSubtitle, report);

    public static AddinReportModel ForMigration(ActivationReport report) =>
        new(UiStrings.Loader_MigrateReportTitle, UiStrings.Loader_MigrateReportSubtitle, report);

    public string Title { get; }

    public string Subtitle { get; }

    public IReadOnlyList<string> SwitchedOff { get; }

    public IReadOnlyList<string> SwitchedOn { get; }

    public IReadOnlyList<string> Moved { get; }

    /// <summary>"DiRoots.One (2027): locked on by your administrator".</summary>
    public IReadOnlyList<string> NotChanged { get; }

    public IReadOnlyList<string> Notes { get; }

    public IReadOnlyList<AddinCheckLine> Missing { get; }

    public IReadOnlyList<string> RequiredOff { get; }

    public bool HasSwitchedOff => SwitchedOff.Count > 0;

    public bool HasSwitchedOn => SwitchedOn.Count > 0;

    public bool HasMoved => Moved.Count > 0;

    public bool HasNotChanged => NotChanged.Count > 0;

    public bool HasNotes => Notes.Count > 0;

    public bool HasMissing => Missing.Count > 0;

    public bool HasRequiredOff => RequiredOff.Count > 0;

    /// <summary>Nothing changed and nothing needs attention.</summary>
    public bool IsEmpty => !(HasSwitchedOff || HasSwitchedOn || HasMoved || HasNotChanged || HasNotes || HasMissing || HasRequiredOff);

    public string NothingText { get; init; } = UiStrings.Loader_ReportNothing;

    /// <summary>Every report says when the changes apply (spec #18, In Scope 5).</summary>
    public string NextStartNote => UiStrings.Loader_ConfirmRestart;

    public System.Windows.Input.ICommand OpenUrlCommand { get; }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v));
}

/// <summary>
/// Opens a profile's add-in link in the default browser (spec #16: <c>target=_blank</c> becomes
/// shell execute). A shared profile is untrusted, so only absolute http and https links open.
/// </summary>
public static class ExternalLinks
{
    public static bool IsOpenable(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public static void Open(string? url)
    {
        if (!IsOpenable(url))
        {
            Log.Warning("Loader: refused to open a non-http link {Url}", url);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url!.Trim()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Loader: could not open {Url}", url);
        }
    }
}
