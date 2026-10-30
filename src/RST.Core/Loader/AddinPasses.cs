// rst-c's own (not vendored): the add-in file operations the Loader drives (spec #16, WU3),
// behind one seam so the load orchestration can be tested without real .addin folders. The
// file-system implementation calls the same RST.Core classes LoaderBridge called.

using RST.Core.AddIns;
using RST.Core.Profiles;
using RST.Core.Scanning;

namespace RST.Core.Loader;

/// <summary>Scans, classifies, disables and restores .addin manifests for one Revit version.</summary>
public interface IAddinPasses
{
    /// <summary><see cref="RequiredAddinQa.Classify(string, IReadOnlyList{RequiredAddin}?, IReadOnlyList{string}?)"/>.</summary>
    IReadOnlyList<RequiredAddinQaResult> Classify(IReadOnlyList<RequiredAddin> required, IReadOnlyList<string> ribbonTabs);

    /// <summary>Renames the required, disabled manifests back to .addin.</summary>
    RestoreResult RestoreRequired(IReadOnlyList<RequiredAddin> disabledRequired, Action<string, Exception> onError);

    /// <summary>Renames every writable, non-required .addin to .addin.RSTdisabled.</summary>
    DisableResult DisableNonRequired(IReadOnlyList<RequiredAddin> required, Action<string, Exception> onError);

    /// <summary>The disable preview (what a disable pass would do), with no side effect.</summary>
    DisablePreview Preview(IReadOnlyList<RequiredAddin> required);

    /// <summary>Renames every .addin.RSTdisabled back to .addin.</summary>
    RestoreResult RestoreAll(Action<string, Exception> onError);

    /// <summary>Every manifest on the search paths (enabled and disabled).</summary>
    IReadOnlyList<AddinManifest> Scan(Action<string, Exception> onSkip);
}

/// <summary>The real passes over the running Revit version's add-in search paths.</summary>
public sealed class FileSystemAddinPasses(string revitVersion) : IAddinPasses
{
    public string RevitVersion { get; } = revitVersion ?? "";

    public IReadOnlyList<RequiredAddinQaResult> Classify(IReadOnlyList<RequiredAddin> required, IReadOnlyList<string> ribbonTabs) =>
        RequiredAddinQa.Classify(RevitVersion, required, ribbonTabs);

    public RestoreResult RestoreRequired(IReadOnlyList<RequiredAddin> disabledRequired, Action<string, Exception> onError) =>
        AddinDisabler.RestoreRequired(RevitVersion, disabledRequired, onError);

    public DisableResult DisableNonRequired(IReadOnlyList<RequiredAddin> required, Action<string, Exception> onError) =>
        AddinDisabler.DisableNonRequired(RevitVersion, required, onError);

    public DisablePreview Preview(IReadOnlyList<RequiredAddin> required) =>
        DisablePreviewBuilder.Build(RevitVersion, required);

    public RestoreResult RestoreAll(Action<string, Exception> onError) =>
        AddinDisabler.RestoreAll(RevitVersion, onError);

    public IReadOnlyList<AddinManifest> Scan(Action<string, Exception> onSkip) =>
        AddinDirectoryScanner.Scan(RevitVersion, onSkip);
}
