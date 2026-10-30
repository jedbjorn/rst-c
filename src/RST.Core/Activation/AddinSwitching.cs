// rst-c's own (not vendored): the one entry point the Loader (and, in v3.1, the tray service,
// feature #17) uses for add-in switching (spec #18): scan, plan, execute, and the one-time v3.0
// migration. Host-agnostic: no WPF, no Revit types, no UI thread.

namespace RST.Core.Activation;

/// <summary>Scans, plans and executes add-in switches. Tests pass a fake.</summary>
public interface IAddinSwitching
{
    /// <summary>The add-ins of every installed Revit year, as they are now.</summary>
    AddinInventory Inventory();

    /// <summary>What <paramref name="policy"/> would change (pure: nothing is written).</summary>
    ActivationPlan Plan(AddinInventory inventory, ActivationPolicy policy);

    /// <summary>Runs the confirmed plan and reports every change.</summary>
    ActivationReport Execute(ActivationPlan plan);
}

/// <summary>The real switching over the installed Revit years and rst-c's record.</summary>
public sealed class AddinSwitching : IAddinSwitching
{
    private readonly AddinRoots _roots;
    private readonly Func<IReadOnlyList<RevitYear>> _years;
    private readonly string _ledgerPath;
    private readonly IActivationFileSystem? _fs;
    private readonly Action<string, Exception>? _onSkip;

    /// <param name="roots">The real folders when null.</param>
    /// <param name="years">The installed years when null (decision #10 detection).</param>
    /// <param name="ledgerPath">%AppData%\RST\addin_switches.json when null.</param>
    /// <param name="fs">The real file system when null.</param>
    /// <param name="onSkip">Called for each manifest or bundle that could not be read.</param>
    public AddinSwitching(
        AddinRoots? roots = null,
        Func<IReadOnlyList<RevitYear>>? years = null,
        string? ledgerPath = null,
        IActivationFileSystem? fs = null,
        Action<string, Exception>? onSkip = null)
    {
        _roots = roots ?? AddinRoots.FromEnvironment();
        _years = years ?? (() => RevitInstallations.Detect(_roots));
        _ledgerPath = ledgerPath ?? ActivationLedger.DefaultPath;
        _fs = fs;
        _onSkip = onSkip;
    }

    public AddinInventory Inventory() => AddinInventory.Scan(_roots, _years(), _onSkip);

    public ActivationPlan Plan(AddinInventory inventory, ActivationPolicy policy) =>
        ActivationPlanner.Plan(inventory, policy, ActivationLedger.Load(_ledgerPath));

    public ActivationReport Execute(ActivationPlan plan) => new ActivationExecutor(_roots, _ledgerPath, _fs).Execute(plan);
}

public static class AddinSwitchingExtensions
{
    /// <summary>
    /// Moves v3.0's renamed manifests into the settings file (spec #18, In Scope 9). Null when there
    /// is nothing to move, so the report shows once: the first time, and only when something moved.
    /// </summary>
    public static ActivationReport? MigrateLegacy(this IAddinSwitching switching, AddinInventory? inventory = null)
    {
        ArgumentNullException.ThrowIfNull(switching);
        var inv = inventory ?? switching.Inventory();
        var plan = switching.Plan(inv, new ActivationPolicy(ActivationIntent.MigrateLegacy, false, []));
        return plan.HasChanges ? switching.Execute(plan) : null;
    }
}
