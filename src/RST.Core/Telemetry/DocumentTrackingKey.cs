// DocumentTrackingKey.cs — the stable per-document key behind the
// throttle caps and the view-activation gate.
//
// Spec Throttling: the per-document caps key on the DOCUMENT, and a
// Save As must neither reset them nor make the next view switch look
// like an active-document change. CreationGUID is stamped when the
// model is created and survives Save / Save As, so it is the primary
// key; path and title re-key on Save As and serve only as degraded
// fallbacks when the GUID is unreadable. Prefixes keep the three
// namespaces from colliding (a path can never alias a guid key).
//
// Known limit: detached copies of one model share a CreationGUID and
// therefore a key — their caps collapse together, which only ever means
// fewer events, never more.

namespace RST.Core.Telemetry;

public static class DocumentTrackingKey
{
    public const string Untitled = "untitled";

    public static string Derive(string? creationGuid, string? pathName, string? title)
    {
        if (!string.IsNullOrEmpty(creationGuid)) return "guid:" + creationGuid;
        if (!string.IsNullOrEmpty(pathName)) return "path:" + pathName;
        if (!string.IsNullOrEmpty(title)) return "title:" + title;
        return Untitled;
    }
}
