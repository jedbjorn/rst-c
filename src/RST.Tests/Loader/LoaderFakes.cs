// rst-c's own (not vendored): fakes for the Loader services' seams (spec #16, WU3).
// RST.RuntimeTests compiles this file too.

using System.IO;
using RST.Core.AddIns;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.Core.Scanning;

namespace RST.Tests.Loader;

/// <summary>Scripted add-in passes: no folder is scanned and nothing is renamed.</summary>
public sealed class FakeAddinPasses : IAddinPasses
{
    public Func<IReadOnlyList<RequiredAddin>, IReadOnlyList<RequiredAddinQaResult>> OnClassify { get; set; } =
        required => required.Select(r => new RequiredAddinQaResult(r, RequiredAddinStatus.InstalledActive, null)).ToList();

    public RestoreResult RestoreRequiredResult { get; set; } = new(0, 0, [], []);

    public DisableResult DisableResult { get; set; } = new(0, 0, 0, 0, [], []);

    public Exception? DisableThrows { get; set; }

    public Exception? ClassifyThrows { get; set; }

    public DisablePreview PreviewResult { get; set; } = new([], [], [], []);

    public RestoreResult RestoreAllResult { get; set; } = new(0, 0, [], []);

    public Exception? RestoreAllThrows { get; set; }

    public IReadOnlyList<AddinManifest> ScanResult { get; set; } = [];

    public List<string> Calls { get; } = [];

    public List<IReadOnlyList<RequiredAddin>> RestoredRequired { get; } = [];

    public int? ClassifyThreadId { get; private set; }

    public IReadOnlyList<RequiredAddinQaResult> Classify(IReadOnlyList<RequiredAddin> required, IReadOnlyList<string> ribbonTabs)
    {
        Calls.Add("Classify");
        ClassifyThreadId = Environment.CurrentManagedThreadId;
        if (ClassifyThrows is not null) throw ClassifyThrows;
        return OnClassify(required);
    }

    public RestoreResult RestoreRequired(IReadOnlyList<RequiredAddin> disabledRequired, Action<string, Exception> onError)
    {
        Calls.Add("RestoreRequired");
        RestoredRequired.Add(disabledRequired);
        return RestoreRequiredResult;
    }

    public DisableResult DisableNonRequired(IReadOnlyList<RequiredAddin> required, Action<string, Exception> onError)
    {
        Calls.Add("DisableNonRequired");
        if (DisableThrows is not null) throw DisableThrows;
        return DisableResult;
    }

    public DisablePreview Preview(IReadOnlyList<RequiredAddin> required)
    {
        Calls.Add("Preview");
        return PreviewResult;
    }

    public RestoreResult RestoreAll(Action<string, Exception> onError)
    {
        Calls.Add("RestoreAll");
        if (RestoreAllThrows is not null) throw RestoreAllThrows;
        return RestoreAllResult;
    }

    public IReadOnlyList<AddinManifest> Scan(Action<string, Exception> onSkip)
    {
        Calls.Add("Scan");
        return ScanResult;
    }

    public static AddinManifest Manifest(string fileName, bool disabled = false, string? name = null, string? id = null) =>
        new(Path.Combine("C:", "Addins", fileName), fileName, disabled,
            [new AddinEntry("Application", "x.dll", id, name, null, null)]);
}

/// <summary>Records every Schedule call; can be told to throw.</summary>
public sealed class FakeScheduler : IProfileSwitchScheduler
{
    public List<Profile?> Scheduled { get; } = [];

    public Exception? Throws { get; set; }

    public int? ThreadId { get; private set; }

    public void Schedule(Profile? profile)
    {
        ThreadId = Environment.CurrentManagedThreadId;
        if (Throws is not null) throw Throws;
        Scheduled.Add(profile);
    }
}

/// <summary>A temp %AppData%\RST stand-in, deleted on dispose.</summary>
public sealed class TempLoaderRoot : IDisposable
{
    public TempLoaderRoot(string? root = null)
    {
        Root = root ?? Path.Combine(Path.GetTempPath(), "rst-loader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "profiles"));
        Paths = LoaderPaths.Under(Root);
    }

    public string Root { get; }

    public LoaderPaths Paths { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Saves <paramref name="profile"/> under profiles\ and returns its file name.</summary>
    public string Add(Profile profile) => ProfileStore.Save(profile, Paths.ProfilesDir);

    public static Profile Profile(string name, string? id = null, string tab = "Tab", params Panel[] panels) => new()
    {
        Id = id ?? Guid.NewGuid().ToString(),
        ProfileName = name,
        Tab = tab,
        ExportDate = "2026-10-01",
        Panels = [.. panels],
    };
}
