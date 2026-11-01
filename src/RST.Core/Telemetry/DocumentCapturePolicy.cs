// rst-c's own: activity capture is for projects and project templates, never families.
namespace RST.Core.Telemetry;

public static class DocumentCapturePolicy
{
    /// <summary>Unknown document flags fail closed; unsaved and cloud projects need no local path.</summary>
    public static bool IsProject(bool? isLinked, bool? isFamilyDocument) =>
        isLinked == false && isFamilyDocument == false;

    /// <summary>DocumentOpening has only a path. Reject family files before any opening record is written,
    /// while preserving unknown and cloud paths for the document gate to classify after opening.</summary>
    public static bool AllowsOpening(string? path) =>
        path is null || !(path.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase) ||
                          path.EndsWith(".rft", StringComparison.OrdinalIgnoreCase));
}
