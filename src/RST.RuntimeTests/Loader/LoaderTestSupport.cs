// rst-c's own (not vendored): fakes for the Profile Loader view-model and window tests
// (spec #16, WU3; the add-in plan and report of spec #18): a scripted backend whose plans come from
// the real planner over a scripted inventory, one ordered log for every dialog, and a manual clock.

using RST.Core.Activation;
using RST.Core.Colors;
using RST.Core.Configuration;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.Tests.Loader;
using RST.UI.Dialogs;
using RST.UI.Loader;

namespace RST.RuntimeTests.Loader;

/// <summary>An in-memory backend: scripted answers, and a log of every call in order.</summary>
public sealed class FakeLoaderBackend : IProfileLoaderBackend
{
    public string RevitVersion { get; set; } = "2026";

    public IReadOnlyList<string> AllTabs { get; set; } = [];

    public List<ProfileEntry> Profiles { get; } = [];

    public ActiveProfile Active { get; set; } = new() { ProfileName = "BlankRST", Blank = true };

    public UserProfilePrefs Prefs { get; set; } = new();

    public Dictionary<string, AddinLookupEntry> Lookup { get; } = new();

    /// <summary>The add-ins of every installed year.</summary>
    public AddinInventory Inventory { get; set; } = AddinInventory.Empty;

    /// <summary>rst-c's own record, for the default plan.</summary>
    public ActivationLedger Ledger { get; set; } = new();

    public List<string> Calls { get; } = [];

    public List<(string Id, IReadOnlyList<string> Hidden, bool Disable, bool Adopted)> PrefsWrites { get; } = [];

    public List<(string Id, int? Opacity)> OpacityWrites { get; } = [];

    public List<LoadRequest> LoadRequests { get; } = [];

    public List<ActivationPlan> Plans { get; } = [];

    public Func<string, ImportOutcome> OnImport { get; set; } = _ => ImportOutcome.Fail("not scripted");

    /// <summary>Null plans with the real planner over <see cref="Inventory"/> and <see cref="Ledger"/>.</summary>
    public Func<string, string?, bool, LoadPlanOutcome>? OnPlan { get; set; }

    /// <summary>Null reports every change of the request's plan Done.</summary>
    public Func<LoadRequest, Task<LoadResult>>? OnLoad { get; set; }

    public Func<UnloadOutcome> OnUnload { get; set; } = () => new UnloadOutcome(true, null, ActivationReport.Empty(ActivationIntent.Unload));

    public Func<string, string?, DeleteOutcome> OnDelete { get; set; } = (_, _) => new DeleteOutcome(true, null, false);

    public Func<RestoreOutcome> OnRestore { get; set; } = () => new RestoreOutcome(true, null, ActivationReport.Empty(ActivationIntent.RestoreAll));

    public Func<ActivationReport?> OnMigrate { get; set; } = () => null;

    public IReadOnlyList<ProfileEntry> GetProfiles()
    {
        Calls.Add("GetProfiles");
        return [.. Profiles];
    }

    public ActiveProfile GetActiveProfile()
    {
        Calls.Add("GetActiveProfile");
        return Active;
    }

    public UserProfilePrefs GetPrefs()
    {
        Calls.Add("GetPrefs");
        return Prefs;
    }

    public IReadOnlyDictionary<string, AddinLookupEntry> GetAddinLookup()
    {
        Calls.Add("GetAddinLookup");
        return Lookup;
    }

    public AddinInventory GetAddinInventory()
    {
        Calls.Add("GetAddinInventory");
        return Inventory;
    }

    public OpResult RecordPrefs(string profileId, IReadOnlyList<string> hiddenTabs, bool disableUnused, bool presetsAdopted)
    {
        Calls.Add("RecordPrefs");
        PrefsWrites.Add((profileId, [.. hiddenTabs], disableUnused, presetsAdopted));
        return OpResult.Success;
    }

    public OpResult RecordOpacityOverride(string profileId, int? opacity)
    {
        Calls.Add("RecordOpacityOverride");
        OpacityWrites.Add((profileId, opacity));
        return OpResult.Success;
    }

    public ImportOutcome Import(string zipPath)
    {
        Calls.Add("Import");
        return OnImport(zipPath);
    }

    public LoadPlanOutcome PlanLoad(string name, string? id, bool disableUnused)
    {
        Calls.Add("PlanLoad");
        if (OnPlan is not null) return OnPlan(name, id, disableUnused);
        var profile = Profiles.Select(p => p.Profile).FirstOrDefault(p => !string.IsNullOrEmpty(id) && p.Id == id)
                      ?? Profiles.Select(p => p.Profile).FirstOrDefault(p => p.ProfileName == name);
        if (profile is null) return new LoadPlanOutcome("Profile not found: " + name, null);
        var plan = ActivationPlanner.Plan(Inventory, ActivationPolicy.Load(profile.RequiredAddins, disableUnused), Ledger);
        Plans.Add(plan);
        return new LoadPlanOutcome(null, plan);
    }

    public Task<LoadResult> LoadAsync(LoadRequest request)
    {
        Calls.Add("Load");
        LoadRequests.Add(request);
        if (OnLoad is not null) return OnLoad(request);
        var report = request.Plan is null ? ActivationReport.Empty(ActivationIntent.Load) : FakeAddinSwitching.AllDone(request.Plan);
        return Task.FromResult(new LoadResult(true, [], report.AnyDone, report.Outcomes, null, report.Notes));
    }

    public UnloadOutcome Unload()
    {
        Calls.Add("Unload");
        return OnUnload();
    }

    public DeleteOutcome Delete(string name, string? id)
    {
        Calls.Add("Delete");
        return OnDelete(name, id);
    }

    public RestoreOutcome RestoreAll()
    {
        Calls.Add("RestoreAll");
        return OnRestore();
    }

    public ActivationReport? MigrateLegacy()
    {
        Calls.Add("MigrateLegacy");
        return OnMigrate();
    }

    public FakeLoaderBackend With(params Profile[] profiles)
    {
        foreach (var p in profiles) Profiles.Add(new ProfileEntry(p.ProfileName + "_" + p.Id + ".json", p));
        return this;
    }

    public FakeLoaderBackend ActiveIs(Profile p, string[]? hidden = null, bool disable = false)
    {
        Active = ActiveProfile.FromProfile(p, p.ProfileName + "_" + p.Id + ".json", hidden ?? [], disable);
        return this;
    }
}

/// <summary>Every dialog (family and Loader) in one ordered log, with scripted answers.</summary>
public sealed class LoaderTestDialogs : IDialogService, ILoaderDialogs
{
    public List<string> Log { get; } = [];

    public Queue<bool> ConfirmAnswers { get; } = new();

    public List<(string Title, string Message, string ConfirmText, bool Danger, string AutomationId)> Confirms { get; } = [];

    public List<(string Title, string Message, string AutomationId)> Messages { get; } = [];

    public bool ConfirmAddinChangesAnswer { get; set; } = true;

    public List<ConfirmAddinChangesModel> AddinChanges { get; } = [];

    public List<AddinReportModel> AddinReports { get; } = [];

    public string? OpenResult { get; set; }

    public List<(string Title, string Filter)> Opens { get; } = [];

    public Action? DuringAddinReport { get; set; }

    public void ShowMessage(string title, string message, string automationId = MessageDialog.MessageId)
    {
        Log.Add("Message:" + title);
        Messages.Add((title, message, automationId));
    }

    public bool Confirm(string title, string message, IReadOnlyList<string> items, string confirmText, bool danger, string automationId)
    {
        Log.Add("Confirm:" + title);
        Confirms.Add((title, message, confirmText, danger, automationId));
        return ConfirmAnswers.Count > 0 && ConfirmAnswers.Dequeue();
    }

    public DialogChoice Choose(string title, string message, string primaryText, string secondaryText, string automationId)
    {
        Log.Add("Choose:" + title);
        return DialogChoice.Cancel;
    }

    public Rgb? PickColor(Rgb current, IReadOnlyList<Rgb> palette, string title) => null;

    public string? OpenFile(string title, string filter)
    {
        Log.Add("Open:" + title);
        Opens.Add((title, filter));
        return OpenResult;
    }

    public string? SaveFile(string title, string filter, string fileName) => null;

    public bool ConfirmAddinChanges(ConfirmAddinChangesModel model)
    {
        Log.Add("ConfirmAddinChanges");
        AddinChanges.Add(model);
        return ConfirmAddinChangesAnswer;
    }

    public void ShowAddinReport(AddinReportModel model)
    {
        Log.Add("AddinReport:" + model.Title);
        AddinReports.Add(model);
        DuringAddinReport?.Invoke();
    }
}

/// <summary>A manual clock for the 3 s close and the toasts: nothing runs until <see cref="Advance"/>.</summary>
public sealed class ManualClock
{
    private readonly List<Pending> _pending = [];

    public TimeSpan Now { get; private set; }

    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        var p = new Pending(Now + delay, action);
        _pending.Add(p);
        return p;
    }

    public IReadOnlyList<TimeSpan> PendingDelays => _pending.Where(p => !p.Cancelled && !p.Ran).Select(p => p.Due - Now).ToList();

    public void Advance(TimeSpan by)
    {
        Now += by;
        foreach (var p in _pending.Where(p => !p.Cancelled && !p.Ran && p.Due <= Now).ToList())
        {
            p.Ran = true;
            p.Action();
        }
    }

    private sealed class Pending(TimeSpan due, Action action) : IDisposable
    {
        public TimeSpan Due { get; } = due;

        public Action Action { get; } = action;

        public bool Cancelled { get; private set; }

        public bool Ran { get; set; }

        public void Dispose() => Cancelled = true;
    }
}

/// <summary>The realistic three-profile set the view-model tests and the screenshots use.</summary>
public static class LoaderFixtures
{
    public const string ArchId = "1f0c6a52-6b0e-4d6f-9a3e-2c8d4b7e9a10";
    public const string StructId = "7b2e9d14-3c5a-4f81-b6d0-8e1a2f4c6b38";
    public const string MepId = "c3d4e5f6-0718-4293-a4b5-c6d7e8f90a1b";

    public static readonly string[] Ribbon =
    [
        "Architecture", "Structure", "Systems", "Insert", "Annotate", "Analyze", "Massing & Site", "Collaborate",
        "View", "Manage", "Add-Ins", "Modify", "Enscape", "Kinship Tools", "Studio Arch",
    ];

    private static Slot Tool(string name, string icon, string sourceTab = "Architecture") =>
        new() { SlotType = "tool", Name = name, CommandId = "ID_" + name.ToUpperInvariant(), SourceTab = sourceTab, IconFile = icon };

    public static Profile Architecture() => new()
    {
        Id = ArchId,
        ProfileName = "Architecture",
        Tab = "Studio Arch",
        MinVersion = "2.2.0",
        ExportDate = "2026-09-14",
        PanelOpacity = 85,
        RequiredAddins =
        [
            new RequiredAddin { TabName = "Architecture" },
            new RequiredAddin { TabName = "Enscape", AddinFile = "Enscape.addin", Url = "https://enscape3d.com" },
        ],
        Panels =
        [
            new Panel { Name = "Model", Color = "#4f8ef7", Slots = [Tool("Wall", "pack:building_office_blue"), Tool("Door", "pack:house_purple"), Tool("Level", "pack:level_green")] },
            new Panel { Name = "Render", Color = "#10b981", Slots = [Tool("Enscape", "pack:camera_orange", "Enscape")] },
            new Panel { Name = "Later", Color = "#8b5cf6", Slots = [] },
        ],
        Presets = new ProfilePresets { HiddenTabs = ["Analyze"] },
    };

    public static Profile Structure() => new()
    {
        Id = StructId,
        ProfileName = "Structure",
        Tab = "Studio Struct",
        MinVersion = "2.1.0",
        ExportDate = "2026-08-02",
        PanelOpacity = 60,
        RequiredAddins =
        [
            new RequiredAddin { TabName = "Structure" },
            new RequiredAddin { TabName = "Kinship", AddinFile = "Kinship.addin" },
            new RequiredAddin { TabName = "pyRevit", AddinFile = "pyRevit.addin", Url = "https://github.com/pyrevitlabs/pyRevit/releases" },
            new RequiredAddin { TabName = "Lumion", AddinFile = "Lumion.addin", Url = "https://lumion.com" },
            new RequiredAddin { TabName = "Guardian" },
            new RequiredAddin { TabName = "Twinmotion", Url = "https://twinmotion.com" },
        ],
        Panels =
        [
            new Panel
            {
                Name = "Frame", Color = "#ef4444",
                Slots = [Tool("Beam", "pack:parallel_red", "Structure"), Tool("Column", "pack:box_stack_dark_grey", "Structure"), Tool("Brace", "pack:arrow_45_orange", "Structure")],
            },
            new Panel
            {
                Name = "Review", Color = "#14b8a6",
                Slots = [Tool("Kinship", "pack:box_open_blue", "Kinship Tools"), new Slot { SlotType = "stack", Name = "Checks" }, Tool("Notes", "custom.png", "Custom")],
            },
            new Panel { Name = "Links", Color = "#f59e0b", Slots = [Tool("Wiki", "pack:link_external", "Custom"), Tool("Help", "pack:question_light_grey", "Custom")] },
        ],
        Stacks = new()
        {
            ["Checks"] = new Stack { Tools = [new Slot { Name = "Warnings", CommandId = "W" }, new Slot { Name = "Audit", CommandId = "A" }, new Slot { Name = "Purge", CommandId = "P" }] },
        },
        Presets = new ProfilePresets { HiddenTabs = ["Massing & Site", "Collaborate"], DisableUnusedAddins = true },
    };

    public static Profile Mep() => new()
    {
        Id = MepId,
        ProfileName = "MEP Coordination",
        Tab = "Studio MEP",
        MinVersion = "2.0.0",
        ExportDate = "2026-07-21",
        PanelOpacity = 100,
        Panels = [new Panel { Name = "Systems", Color = "#64748b", Slots = [Tool("Duct", "pack:sliders_dark_grey", "Systems"), Tool("Pipe", "pack:sync_green", "Systems")] }],
    };

    /// <summary>
    /// The add-ins of Revit 2026 and 2027 behind the fixture: RST and Autodesk's Batch Print
    /// (protected), pyRevit and Enscape on, Lumion switched off by the user, Guardian locked off and
    /// (2027) Revizto locked on by the administrator, DiRoots.One (machine-wide) and Ideate on.
    /// </summary>
    public static AddinInventory Inventory() => FakeAddinSwitching.InventoryOf(
        FakeAddinSwitching.Item(2026, "RST", fileName: "RST.addin", id: InventoryItem.RstClientId, vendor: "RSTC"),
        FakeAddinSwitching.Item(2026, "Batch Print", AddinScope.Machine, vendor: "ADSK"),
        FakeAddinSwitching.Item(2026, "PyRevitLoader", AddinScope.Machine, fileName: "pyRevit.addin"),
        FakeAddinSwitching.Item(2026, "Enscape", AddinScope.Machine),
        FakeAddinSwitching.Item(2026, "Lumion LiveSync", fileName: "Lumion.addin", off: true),
        FakeAddinSwitching.Item(2026, "Guardian", AddinScope.Machine, adminLock: AdminLock.AlwaysDisabled),
        FakeAddinSwitching.Item(2026, "DiRoots.One", AddinScope.Machine, vendor: "www.diroots.com"),
        FakeAddinSwitching.Item(2026, "Ideate BIMLink", fileName: "IdeateBIMLink.addin"),
        FakeAddinSwitching.Item(2027, "RST", fileName: "RST.addin", id: InventoryItem.RstClientId, vendor: "RSTC"),
        FakeAddinSwitching.Item(2027, "Enscape", AddinScope.Machine),
        FakeAddinSwitching.Item(2027, "DiRoots.One", AddinScope.Machine, vendor: "www.diroots.com"),
        FakeAddinSwitching.Item(2027, "Ideate BIMLink", fileName: "IdeateBIMLink.addin"),
        FakeAddinSwitching.Item(2027, "Revizto", adminLock: AdminLock.AlwaysEnabled));

    /// <summary>
    /// Architecture is active; Structure has saved prefs (Massing &amp; Site hidden, disable on);
    /// MEP has none. Lookup, the add-in inventory and ribbon tabs put Structure's add-ins in every
    /// state: Native, Loaded (on the ribbon), Loaded (installed and on), Disabled, Locked, Not Found.
    /// </summary>
    public static FakeLoaderBackend Backend()
    {
        var backend = new FakeLoaderBackend { AllTabs = Ribbon, Inventory = Inventory() }.With(Architecture(), Structure(), Mep());
        backend.ActiveIs(Architecture(), ["Analyze"]);
        backend.Prefs.Profiles[ArchId] = new UserProfilePrefsEntry { HiddenTabs = ["Analyze"], PresetsAdopted = true, PanelOpacityOverride = 70 };
        backend.Prefs.Profiles[StructId] = new UserProfilePrefsEntry { HiddenTabs = ["Massing & Site"], DisableUnusedAddins = true, PresetsAdopted = true };
        backend.Lookup["Kinship"] = new AddinLookupEntry("Kinship", "Kinship.addin", "");
        backend.Lookup["Lumion"] = new AddinLookupEntry("Lumion LiveSync", "Lumion.addin", "https://lumion.com");
        backend.Lookup["Guardian"] = new AddinLookupEntry("Guardian", "Guardian.addin", "");
        return backend;
    }

    /// <summary>rst-c switched Ideate BIMLink off in both years (its record says so).</summary>
    public static void IdeateSwitchedOffByRst(FakeLoaderBackend backend)
    {
        var items = backend.Inventory.Items.Select(i => i.Name == "Ideate BIMLink" ? i with { DisabledIds = i.AddinIds } : i).ToList();
        backend.Inventory = new AddinInventory(backend.Inventory.Years, items);
        foreach (var i in items.Where(i => i.Name == "Ideate BIMLink"))
            backend.Ledger.Add(new LedgerEntry(i.Year, i.AddinIds[0], i.Name, LedgerKind.Setting, i.ManifestPath, DateTime.UtcNow));
    }
}
