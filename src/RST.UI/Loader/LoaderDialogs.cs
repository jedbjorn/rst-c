// rst-c's own (not vendored): the Profile Loader's two own dialogs (spec #16, WU3), the HTML
// overlays "Confirm Add-in Changes" (profile_loader.html 884-908, 1698-1731) and the "Add-in
// Check" QA modal (869-881, 1476-1543), as family-styled windows behind a seam the view-model
// tests fake.

using System.Diagnostics;
using System.Windows;
using RST.Core.Loader;
using RST.UI.Mvvm;
using RST.UI.Resources;
using Serilog;

namespace RST.UI.Loader;

/// <summary>The Loader's own modal dialogs.</summary>
public interface ILoaderDialogs
{
    /// <summary>"Confirm Add-in Changes": true for Confirm &amp; Load, false for Cancel.</summary>
    bool ConfirmAddinChanges(ConfirmAddinChangesModel model);

    /// <summary>The "Add-in Check" QA dialog; returns when the user clicks Continue.</summary>
    void ShowAddinCheck(AddinCheckModel model);
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

    public void ShowAddinCheck(AddinCheckModel model)
    {
        var dialog = new AddinCheckWindow(model) { Owner = Owner };
        dialog.ShowDialog();
    }
}

/// <summary>What "Confirm Add-in Changes" lists: Staying Active, Will Be Disabled, and Try Disable when any.</summary>
public sealed class ConfirmAddinChangesModel
{
    public ConfirmAddinChangesModel(IEnumerable<string> staying, IEnumerable<string> disabling, IEnumerable<string> tryDisable)
    {
        Staying = staying.ToList();
        Disabling = disabling.ToList();
        TryDisable = tryDisable.ToList();
    }

    /// <summary>The rows from the disable preview (display name, else "Unknown").</summary>
    public static ConfirmAddinChangesModel From(DisablePreviewOutcome preview) => new(
        preview.Staying.Select(Label), preview.Disabling.Select(Label), preview.TryDisable.Select(Label));

    public IReadOnlyList<string> Staying { get; }

    public IReadOnlyList<string> Disabling { get; }

    public IReadOnlyList<string> TryDisable { get; }

    public bool HasStaying => Staying.Count > 0;

    public bool HasDisabling => Disabling.Count > 0;

    /// <summary>The Try Disable column shows only when it has entries.</summary>
    public bool HasTryDisable => TryDisable.Count > 0;

    private static string Label(DisablePreviewItem item) =>
        string.IsNullOrEmpty(item.DisplayName) ? UiStrings.Loader_Unknown : item.DisplayName;
}

/// <summary>One "Auto-Restored" or "Missing" line of the Add-in Check.</summary>
public sealed record AddinCheckLine(string Text, string? Url)
{
    public bool HasUrl => !string.IsNullOrEmpty(Url);
}

/// <summary>The "Add-in Check" shown after every successful load (QA result).</summary>
public sealed class AddinCheckModel
{
    public AddinCheckModel(string profileName, QaCheck qa, Action<string>? openUrl = null)
    {
        AllClear = qa.AllClear;
        Subtitle = AllClear ? UiStrings.Loader_QaAllClear : UiStrings.Loader_QaReview(profileName);
        Restored = qa.Restored.Select(r =>
        {
            var label = FirstNonEmpty(r.TabName, r.AddinFile) ?? UiStrings.Loader_QaUnknown;
            var sub = !string.IsNullOrEmpty(r.AddinFile) && r.AddinFile != r.TabName ? " — " + r.AddinFile : "";
            return new AddinCheckLine(label + sub, null);
        }).ToList();
        Missing = qa.NotInstalled.Select(n =>
            new AddinCheckLine(FirstNonEmpty(n.TabName, n.AddinFile) ?? UiStrings.Loader_QaUnknown, string.IsNullOrEmpty(n.Url) ? null : n.Url)).ToList();
        var open = openUrl ?? ExternalLinks.Open;
        OpenUrlCommand = new RelayCommand<string>(url => open(url));
    }

    public string Subtitle { get; }

    public bool AllClear { get; }

    public IReadOnlyList<AddinCheckLine> Restored { get; }

    public IReadOnlyList<AddinCheckLine> Missing { get; }

    public bool HasRestored => Restored.Count > 0;

    public bool HasMissing => Missing.Count > 0;

    /// <summary>The restart note appears only when something was auto-restored.</summary>
    public bool ShowRestartNote => HasRestored;

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
