// rst-c's own (not vendored): fakes and fixtures for the Profile Builder view-model and window
// tests (spec #16, WU4): a scripted backend that keeps a profile library in memory, a realistic
// catalogue and profile set, and the 823-command catalogue for the performance budget (AC7).
// Feature #18 (WU4): the backend can keep its profiles in a real folder (OnDisk), where the Custom
// Tools' edit and remove everywhere run RST.Core's CustomToolEditor on the files themselves.

using System.IO;
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

    /// <summary>The profiles folder when the library is on disk (<see cref="OnDisk"/>), else null.</summary>
    public string? ProfilesDir { get; private set; }

    /// <summary>
    /// Moves the library into <paramref name="dir"/>: the profiles are written there, and from then on
    /// GetProfiles reads the folder and Save writes it (ProfileSaveService), as the real backend does.
    /// </summary>
    public FakeBuilderBackend OnDisk(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var entry in Library) ProfileStore.Save(entry.Profile, dir);
        Library.Clear();
        ProfilesDir = dir;
        return this;
    }

    public IReadOnlyList<ProfileEntry> GetProfiles()
    {
        Calls.Add("GetProfiles");
        return ProfilesDir is { } dir ? ProfileStore.List(dir) : [.. Library];
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
        if (ProfilesDir is { } dir)
        {
            var outcome = new ProfileSaveService(dir).Save(profile);
            if (outcome.Saved is { } written) Saved.Add(written);
            return outcome;
        }
        var copy = ProfileEditing.Clone(profile);
        if (string.IsNullOrEmpty(copy.Id)) copy.Id = "new-" + _nextId++;
        if (string.IsNullOrEmpty(copy.ExportDate)) copy.ExportDate = "2026-10-30";
        Saved.Add(copy);
        var fileName = ProfileStore.CanonicalFileName(copy);
        Library.RemoveAll(e => e.Profile.Id == copy.Id || e.FileName == fileName); // a write replaces a file of that name
        Library.Add(new ProfileEntry(fileName, copy));
        return new SaveOutcome(true, null, fileName, copy);
    }

    public List<string> Removed { get; } = [];

    public OpResult RemoveResult { get; set; } = OpResult.Success;

    /// <summary>As the real one: the library entry read from that file goes (unless the removal is scripted to fail).</summary>
    public OpResult RemoveSupersededFile(string fileName)
    {
        Calls.Add("RemoveSupersededFile");
        Removed.Add(fileName);
        if (ProfilesDir is { } dir && RemoveResult.Ok) return new ProfileSaveService(dir).RemoveSupersededFile(fileName);
        if (RemoveResult.Ok) Library.RemoveAll(e => e.FileName == fileName);
        return RemoveResult;
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
        foreach (var p in profiles)
        {
            if (ProfilesDir is { } dir) ProfileStore.Save(p, dir);
            else Library.Add(new ProfileEntry(ProfileStore.CanonicalFileName(p), p));
        }
        return this;
    }

    // ── Custom Tools (feature #18, WU4): the real editor over the folder ──

    /// <summary>Replaces the custom tool operations (a backend that throws, for instance).</summary>
    public Func<string, CustomToolUpdate>? OnCustomToolChange { get; set; }

    /// <summary>Every edit and remove, in order: (verb, link, the working copy's id).</summary>
    public List<(string Verb, string Link, string? WorkingId)> CustomToolChanges { get; } = [];

    private CustomToolEditor Editor() => new(ProfilesDir ?? throw new InvalidOperationException("custom tool edits run on a profiles folder: call OnDisk first"),
        new ProfileSaveService(ProfilesDir));

    public CustomToolPlan PlanCustomTool(string link, Profile? working)
    {
        Calls.Add("PlanCustomTool");
        return Editor().Plan(link, working);
    }

    public CustomToolUpdate EditCustomTool(CustomTool tool, CustomToolEdit edit, Profile? working, string? activeProfileId)
    {
        Calls.Add("EditCustomTool");
        CustomToolChanges.Add(("edit", tool.Link, working?.Id));
        return OnCustomToolChange?.Invoke("edit") ?? Editor().Edit(tool, edit, working, activeProfileId);
    }

    public CustomToolUpdate RemoveCustomTool(string link, Profile? working, string? activeProfileId)
    {
        Calls.Add("RemoveCustomTool");
        CustomToolChanges.Add(("remove", link, working?.Id));
        return OnCustomToolChange?.Invoke("remove") ?? Editor().Remove(link, working, activeProfileId);
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

/// <summary>The panel cards' tiles and stacks by position (feature #18, WU3), for the tests.</summary>
public static class PanelCardTestExtensions
{
    /// <summary>The loose tool at <paramref name="slot"/>, or row <paramref name="row"/> of the stack there.</summary>
    public static ToolTileViewModel Tile(this PanelCardViewModel card, int slot, int row = -1) =>
        row < 0 ? (ToolTileViewModel)card.Items[slot] : ((StackGroupViewModel)card.Items[slot]).Rows[row];

    /// <summary>The stack at <paramref name="slot"/>.</summary>
    public static StackGroupViewModel StackAt(this PanelCardViewModel card, int slot) => (StackGroupViewModel)card.Items[slot];

    /// <summary>"Beam,Column,[Warnings Audit Purge],Notes": the panel's slots, a stack as its tools in brackets.</summary>
    public static string Shape(Profile profile, int panel) => string.Join(",", profile.Panels[panel].Slots.Select(s =>
        PanelStacks.IsStack(s) ? "[" + string.Join(" ", PanelStacks.ToolsOf(profile, s).Select(t => t.Name)) + "]" : s.Name));
}

/// <summary>
/// Scripted answers for the Builder's dialogs. The name dialog types each of <see cref="Names"/> in
/// turn and presses Confirm, as a user would after a refusal; it answers true at the first name
/// that passes, and Cancel (false) when the script runs out. Branding records the call.
/// </summary>
public sealed class FakeBuilderDialogs : IBuilderDialogs
{
    /// <summary>The names typed in the next name dialog, in order (empty: the dialog is cancelled).</summary>
    public Queue<string> Names { get; } = new();

    /// <summary>Every name dialog opened, with what it showed first.</summary>
    public List<(ProfileNameDialogModel Model, string Suggested)> NameDialogs { get; } = [];

    /// <summary>The refusals shown in the name dialogs, in order.</summary>
    public List<string> Refusals { get; } = [];

    public List<ProfileBuilderViewModel> Brandings { get; } = [];

    /// <summary>
    /// What the next stack dialog does: ticks and orders as a user would, then answers Confirm (true)
    /// or Cancel (false). Empty: the dialog is cancelled. Confirm is refused while the model cannot
    /// confirm (New with fewer than 2), as the real button is disabled.
    /// </summary>
    public Queue<Func<StackDialogModel, bool>> Stacks { get; } = new();

    /// <summary>Every stack dialog opened, with the picks it started with.</summary>
    public List<(StackDialogModel Model, IReadOnlyList<StackPick> Initial)> StackDialogs { get; } = [];

    public bool EditStack(StackDialogModel model)
    {
        StackDialogs.Add((model, model.Picks));
        if (Stacks.Count == 0) return false;
        return Stacks.Dequeue()(model) && model.CanConfirm;
    }

    /// <summary>Ticks the listed tools by name, in that order (a stack dialog script step).</summary>
    public static void Tick(StackDialogModel model, params string[] names)
    {
        foreach (var name in names) model.Items.First(i => i.Name == name).IsChecked = true;
    }

    /// <summary>Unticks the listed tools by name.</summary>
    public static void Untick(StackDialogModel model, params string[] names)
    {
        foreach (var name in names) model.Items.First(i => i.Name == name).IsChecked = false;
    }

    public bool EditName(ProfileNameDialogModel model)
    {
        NameDialogs.Add((model, model.Name));
        while (Names.Count > 0)
        {
            model.Name = Names.Dequeue();
            if (model.TryConfirm()) return true;
            Refusals.Add(model.Error!);
        }
        return false;
    }

    public void EditBranding(ProfileBuilderViewModel model) => Brandings.Add(model);

    /// <summary>
    /// What the next Custom Tool dialog does: fills it in as a user would, then answers Confirm (true,
    /// which passes only when the model's checks pass, as the real button) or Cancel. Empty: Cancel.
    /// </summary>
    public Queue<Func<CustomToolDialogModel, bool>> CustomTools { get; } = new();

    /// <summary>Every Custom Tool dialog opened.</summary>
    public List<CustomToolDialogModel> CustomToolDialogs { get; } = [];

    public bool EditCustomTool(CustomToolDialogModel model)
    {
        CustomToolDialogs.Add(model);
        if (CustomTools.Count == 0) return false;
        return CustomTools.Dequeue()(model) && model.TryConfirm();
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
