// rst-c's own (not vendored): fakes and fixtures for the Profile Builder view-model and window
// tests (spec #16, WU4): a scripted backend that keeps a profile library in memory, a realistic
// catalogue and profile set, and the 823-command catalogue for the performance budget (AC7).

using RST.Core.Builder;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.RuntimeTests.Loader;
using RST.RuntimeTests.Support;
using RST.Core.Ribbon;
using RST.UI.Builder;
using RST.UI.Loader;

namespace RST.RuntimeTests.Builder;

/// <summary>An in-memory backend: a profile library, scripted answers, and a log of every call in order.</summary>
public sealed class FakeBuilderBackend : IProfileBuilderBackend
{
    public string RevitVersion { get; set; } = "2026";

    public IReadOnlyList<string> AllTabs { get; set; } = [];

    public IReadOnlyList<CatalogCommand> Catalog { get; set; } = [];

    public List<ProfileEntry> Library { get; } = [];

    public ActiveProfile Active { get; set; } = new() { ProfileName = "BlankRST", Blank = true };

    public Dictionary<string, AddinLookupEntry> Lookup { get; } = new();

    public BrandingState Branding { get; set; } = BrandingState.None;

    public List<string> Calls { get; } = [];

    public List<Profile> Saved { get; } = [];

    public List<(Profile Profile, string Path)> Exports { get; } = [];

    public List<LoadRequest> Loads { get; } = [];

    public List<string> BrandingUrls { get; } = [];

    public List<string> Logos { get; } = [];

    public string? SaveError { get; set; }

    public OpResult ExportResult { get; set; } = OpResult.Success;

    public OpResult UrlResult { get; set; } = OpResult.Success;

    public LogoOutcome LogoResult { get; set; } = new(true, null, "branding.png");

    public OpResult ClearResult { get; set; } = OpResult.Success;

    public Func<LoadRequest, Task<LoadResult>> OnLoad { get; set; } =
        _ => Task.FromResult(new LoadResult(true, [], false, [], QaCheck.Clear));

    private int _nextId = 1;

    public IReadOnlyList<ProfileEntry> GetProfiles()
    {
        Calls.Add("GetProfiles");
        return [.. Library];
    }

    public ActiveProfile GetActiveProfile()
    {
        Calls.Add("GetActiveProfile");
        return Active;
    }

    public IReadOnlyDictionary<string, AddinLookupEntry> GetAddinLookup()
    {
        Calls.Add("GetAddinLookup");
        return Lookup;
    }

    /// <summary>As the real save: a copy gets an id and a date, and replaces the library entry of that id.</summary>
    public SaveOutcome Save(Profile profile)
    {
        Calls.Add("Save");
        if (SaveError is not null) return SaveOutcome.Fail(SaveError);
        var copy = ProfileEditing.Clone(profile);
        if (string.IsNullOrEmpty(copy.Id)) copy.Id = "new-" + _nextId++;
        if (string.IsNullOrEmpty(copy.ExportDate)) copy.ExportDate = "2026-10-30";
        Saved.Add(copy);
        var fileName = ProfileStore.CanonicalFileName(copy);
        Library.RemoveAll(e => e.Profile.Id == copy.Id);
        Library.Add(new ProfileEntry(fileName, copy));
        return new SaveOutcome(true, null, fileName, copy);
    }

    public OpResult Export(Profile profile, string path)
    {
        Calls.Add("Export");
        Exports.Add((ProfileEditing.Clone(profile), path));
        return ExportResult;
    }

    public BrandingState LoadBranding()
    {
        Calls.Add("LoadBranding");
        return Branding;
    }

    public OpResult SaveBrandingUrl(string url)
    {
        Calls.Add("SaveBrandingUrl");
        BrandingUrls.Add(url);
        return UrlResult;
    }

    public LogoOutcome StoreLogo(string sourcePath)
    {
        Calls.Add("StoreLogo");
        Logos.Add(sourcePath);
        return LogoResult;
    }

    public OpResult ClearLogo()
    {
        Calls.Add("ClearLogo");
        return ClearResult;
    }

    public Task<LoadResult> LoadAsync(LoadRequest request)
    {
        Calls.Add("Load");
        Loads.Add(request);
        return OnLoad(request);
    }

    /// <summary>The icon pack; null reads the shipped one (the real images, for the screenshots).</summary>
    public IReadOnlyList<IconPackEntry>? IconPack { get; set; }

    /// <summary>Overrides the pack read (a throwing backend).</summary>
    public Func<IReadOnlyList<IconPackEntry>>? OnIconPack { get; set; }

    public IReadOnlyList<IconPackEntry> GetIconPack()
    {
        Calls.Add("GetIconPack");
        if (OnIconPack is not null) return OnIconPack();
        return IconPack ??= IconPackSource.Read(LoaderAssets.IconPackDirectory);
    }

    public FakeBuilderBackend With(params Profile[] profiles)
    {
        foreach (var p in profiles) Library.Add(new ProfileEntry(ProfileStore.CanonicalFileName(p), p));
        return this;
    }
}

public static class BuilderFixtures
{
    public const string ArchId = LoaderFixtures.ArchId;
    public const string StructId = LoaderFixtures.StructId;

    private static CatalogCommand Native(string id, string name, string tab, string panel) => new(id, name, "Native", tab, panel);

    private static CatalogCommand Third(string id, string name, string tab, string? panel) => new(id, name, "ThirdParty", tab, panel);

    /// <summary>A small realistic catalogue: native Revit tabs and a few vendors.</summary>
    public static IReadOnlyList<CatalogCommand> Catalog() =>
    [
        Native("ID_OBJECTS_WALL", "Wall", "Architecture", "Build"),
        Native("ID_OBJECTS_DOOR", "Door", "Architecture", "Build"),
        Native("ID_OBJECTS_WINDOW", "Window", "Architecture", "Build"),
        Native("ID_OBJECTS_LEVEL", "Level", "Architecture", "Datum"),
        Native("ID_OBJECTS_GRID", "Grid", "Architecture", "Datum"),
        Native("ID_OBJECTS_BEAM", "Beam", "Structure", "Structure"),
        Native("ID_OBJECTS_COLUMN", "Structural Column", "Structure", "Structure"),
        Native("ID_VIEW_SECTION", "Section", "View", "Create"),
        Third("Enscape.Start", "Start Enscape", "Enscape", "Enscape"),
        Third("Enscape.Sync", "Live Updates", "Enscape", "Enscape"),
        Third("Enscape.Assets", "Asset Library", "Enscape", "Assets"),
        Third("CustomCtrl_%Add-Ins%Kinship%KinshipUploadThis2Library", "Upload to Library", "Kinship", null),
        Third("CustomCtrl_%Add-Ins%Kinship%KinshipOpenLibrary", "Open Library", "Kinship", null),
        Third("CustomCtrl_%pyRevit%Analysis%ColorSplasher", "Color Splasher", "pyRevit", "Analysis"),
        Third("CustomCtrl_%pyRevit%Selection%SelectSimilar", "Select Similar", "pyRevit", "Selection"),
    ];

    /// <summary>Architecture (active) and Structure, as the Loader tests' set.</summary>
    public static FakeBuilderBackend Backend()
    {
        var backend = new FakeBuilderBackend
        {
            Catalog = Catalog(),
            AllTabs = LoaderFixtures.Ribbon,
        }.With(LoaderFixtures.Architecture(), LoaderFixtures.Structure());
        backend.Active = ActiveProfile.FromProfile(LoaderFixtures.Architecture(), "Architecture.json", ["Analyze"], true);
        backend.Lookup["Enscape"] = new AddinLookupEntry("Enscape", "Enscape.addin", "https://enscape3d.com");
        backend.Lookup["Kinship"] = new AddinLookupEntry("Kinship", "Kinship.addin", "");
        backend.Lookup["pyRevit"] = new AddinLookupEntry("pyRevit", "pyRevit.addin", "https://github.com/pyrevitlabs/pyRevit/releases");
        return backend;
    }

    /// <summary>
    /// The 823-command catalogue (AC7): 20 native tabs of 5 panels (600 commands, 6 each) and 37
    /// vendor tabs of 3 panels (223 commands), named like real ones so a keystroke matches some of them.
    /// </summary>
    public static IReadOnlyList<CatalogCommand> Large()
    {
        var verbs = new[] { "Wall", "Door", "Window", "Level", "Grid", "Section", "Tag", "Room", "Duct", "Pipe", "Sheet", "Schedule" };
        var list = new List<CatalogCommand>();
        for (var t = 0; t < 20; t++)
            for (var p = 0; p < 5; p++)
                for (var c = 0; c < 6; c++)
                {
                    var verb = verbs[(t * 7 + p * 3 + c) % verbs.Length];
                    list.Add(Native($"ID_{verb.ToUpperInvariant()}_{t}_{p}_{c}", $"{verb} {t}.{p}.{c}", $"Native Tab {t:00}", $"Panel {p}"));
                }
        for (var i = 0; list.Count < 823; i++)
        {
            var verb = verbs[i % verbs.Length];
            list.Add(Third($"CustomCtrl_%Vendor{i % 37:00}%Tools%{verb}{i}", $"{verb} Tool {i}", $"Vendor {i % 37:00}", $"Tools {i % 3}"));
        }
        return list;
    }
}

/// <summary>Scripted answers for the RST Presets and Junk Cleanup modals; the edit runs on the modal's model first.</summary>
public sealed class FakeBuilderDialogs : IBuilderDialogs
{
    public List<PresetsViewModel> Presets { get; } = [];

    public List<CleanupViewModel> Cleanups { get; } = [];

    /// <summary>What the user does in the Presets modal before answering.</summary>
    public Action<PresetsViewModel>? EditPresetsWith { get; set; }

    public bool PresetsAnswer { get; set; } = true;

    /// <summary>What the user does in the Cleanup modal before answering.</summary>
    public Action<CleanupViewModel>? EditCleanupWith { get; set; }

    public bool CleanupAnswer { get; set; } = true;

    public bool EditPresets(PresetsViewModel model)
    {
        Presets.Add(model);
        EditPresetsWith?.Invoke(model);
        return PresetsAnswer;
    }

    public bool EditCleanup(CleanupViewModel model)
    {
        Cleanups.Add(model);
        EditCleanupWith?.Invoke(model);
        return CleanupAnswer;
    }
}

/// <summary>A view-model over a backend, with the fake dialogs and clock, ready to drive.</summary>
public sealed record BuilderHarness(ProfileBuilderViewModel Model, IProfileBuilderBackend Raw, FakeDialogs Dialogs, ManualClock Clock, Queue<Action>? Posted, FakeBuilderDialogs Modals)
{
    public FakeBuilderBackend Backend => (FakeBuilderBackend)Raw;

    /// <param name="backend">The backend; null is the realistic fixture set.</param>
    /// <param name="deferPosts">Queue what the view-model posts (run with <see cref="RunPosted"/>) instead of running it at once.</param>
    public static BuilderHarness Create(IProfileBuilderBackend? backend = null, bool deferPosts = false)
    {
        var b = backend ?? BuilderFixtures.Backend();
        var dialogs = new FakeDialogs();
        var clock = new ManualClock();
        var posted = deferPosts ? new Queue<Action>() : null;
        var modals = new FakeBuilderDialogs();
        var model = new ProfileBuilderViewModel(b, dialogs, clock.Schedule, post: posted is null ? a => a() : posted.Enqueue, builderDialogs: modals);
        model.Initialize();
        return new BuilderHarness(model, b, dialogs, clock, posted, modals);
    }

    /// <summary>Runs every posted action (the dispatcher turn after the current event).</summary>
    public void RunPosted()
    {
        while (Posted is { Count: > 0 }) Posted.Dequeue()();
    }

    public ProfileOptionViewModel Option(string name) =>
        Model.ProfileOptions.Single(o => o.Entry?.Profile.ProfileName == name);

    public CatalogNodeViewModel Command(string id) =>
        Model.AllCatalogRoots.SelectMany(r => r.Flatten()).Single(n => n.Command?.Id == id);
}
