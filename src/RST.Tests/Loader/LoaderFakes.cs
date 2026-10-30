// rst-c's own (not vendored): fakes for the Loader services' seams (spec #16, WU3; the add-in
// switching seam of spec #18). RST.RuntimeTests compiles this file too.

using System.IO;
using RST.Core.Activation;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.Core.Scanning;

namespace RST.Tests.Loader;

/// <summary>
/// Scripted add-in switching: a fixed inventory, the real planner over an in-memory ledger, and an
/// executor that reports every change Done unless told otherwise. Nothing on disk is touched.
/// </summary>
public sealed class FakeAddinSwitching : IAddinSwitching
{
    public AddinInventory InventoryResult { get; set; } = AddinInventory.Empty;

    public ActivationLedger Ledger { get; set; } = new();

    /// <summary>The report for an executed plan; null reports every change Done.</summary>
    public Func<ActivationPlan, ActivationReport>? OnExecute { get; set; }

    public Exception? InventoryThrows { get; set; }

    public Exception? ExecuteThrows { get; set; }

    public List<string> Calls { get; } = [];

    public List<ActivationPolicy> Policies { get; } = [];

    public List<ActivationPlan> Executed { get; } = [];

    public int? ExecuteThreadId { get; private set; }

    public AddinInventory Inventory()
    {
        Calls.Add("Inventory");
        if (InventoryThrows is not null) throw InventoryThrows;
        return InventoryResult;
    }

    public ActivationPlan Plan(AddinInventory inventory, ActivationPolicy policy)
    {
        Calls.Add("Plan:" + policy.Intent);
        Policies.Add(policy);
        return ActivationPlanner.Plan(inventory, policy, Ledger);
    }

    public ActivationReport Execute(ActivationPlan plan)
    {
        Calls.Add("Execute:" + plan.Intent);
        ExecuteThreadId = Environment.CurrentManagedThreadId;
        Executed.Add(plan);
        if (ExecuteThrows is not null) throw ExecuteThrows;
        return OnExecute?.Invoke(plan) ?? AllDone(plan);
    }

    /// <summary>Every change Done, except those the plan already knows are blocked (an admin lock, an unreadable file).</summary>
    public static ActivationReport AllDone(ActivationPlan plan) =>
        new(plan.Intent,
            plan.Changes.Select(e => e.ExpectedBlock is { } block
                ? ActivationOutcome.For(e, OutcomeResult.NotChanged, block)
                : ActivationOutcome.For(e, OutcomeResult.Done)).ToList(),
            plan.Notes.Select(n => n.Text).ToList());

    /// <summary>An inventory of <paramref name="items"/>, every year on the settings file.</summary>
    public static AddinInventory InventoryOf(params InventoryItem[] items) =>
        new(items.Select(i => i.Year).Distinct().Order()
                .Select(y => new InventoryYear(new RevitYear(y, null, null, SwitchMechanism.SettingsFile), "settings-" + y, SettingsFileState.Ok, null, false, "admin-" + y, false))
                .ToList(),
            items);

    /// <summary>One add-in: <paramref name="off"/> switches it off in the settings file.</summary>
    public static InventoryItem Item(
        int year,
        string name,
        AddinScope scope = AddinScope.User,
        string? id = null,
        bool off = false,
        string? vendor = "ACME",
        string? fileName = null,
        bool renamed = false,
        AdminLock adminLock = AdminLock.None,
        SwitchMechanism mechanism = SwitchMechanism.SettingsFile)
    {
        var addinId = AddinIds.Normalize(id) ?? DeterministicId(name);
        var file = fileName ?? name.Replace(" ", "") + ".addin";
        var path = Path.Combine("C:", scope.ToString(), year.ToString(System.Globalization.CultureInfo.InvariantCulture), file);
        return new InventoryItem(year, mechanism, name, vendor, scope, path, file, [addinId], renamed, false, null, null,
            off && mechanism == SwitchMechanism.SettingsFile ? [addinId] : [], adminLock,
            [new AddinEntry("Application", name + ".dll", addinId, name, vendor, null)]);
    }

    /// <summary>A stable GUID per name, so the same add-in has the same id in every year (as on a real machine).</summary>
    public static string DeterministicId(string name)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name));
        return new Guid(bytes).ToString("D");
    }
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
