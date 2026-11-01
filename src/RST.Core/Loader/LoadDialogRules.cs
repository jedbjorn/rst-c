// rst-c's own (not vendored): SC-048 F6, only interrupt a load for add-in changes or something
// to report. Admin-locked changes appear in the Add-in Check without confirmation. File errors
// still need confirmation: the executor retries them and may succeed if the file has recovered.

using RST.Core.Activation;

namespace RST.Core.Loader;

public static class LoadDialogRules
{
    public static bool NeedsConfirmation(ActivationPlan? plan) =>
        plan is not null && plan.Entries.Any(e => e.IsChange && e.ExpectedBlock != ChangeReason.AdminLocked);

    /// <summary>Plan failures are included in the report's notes by the caller.</summary>
    public static bool NeedsReport(ActivationReport report, QaCheck qa) =>
        report.Outcomes.Count > 0 || report.Notes.Count > 0 || !qa.AllClear;
}
