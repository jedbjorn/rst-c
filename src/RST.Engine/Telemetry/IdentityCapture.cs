// IdentityCapture.cs — maps a Revit Document to the raw DocumentIdentity
// wire block (spec Model Identity). Capture raw, resolve nothing: the
// cloud-GUID → central-GUID → creation-GUID priority is a server concern.
//
// Every field read wraps in its own guard → null: an identity gap is
// data, never an exception surfaced to Revit. All reads are cheap
// property reads except BasicFileInfo.Extract, which reads one file
// header and therefore only runs for the full block (doc_opened /
// doc_saved / doc_saved_as) — never on the keys-only hot paths — and
// only against a LocalPathGuard-verified local path, never a UNC share,
// mapped network drive, or cloud pseudo-path (SC-026).

using System;
using Autodesk.Revit.DB;
using RST.Core.Telemetry;

namespace RST.Engine.Telemetry;

internal static class IdentityCapture
{
    /// <summary>
    /// False for null, linked, and unreadable documents. Linked
    /// documents are excluded from telemetry at every doc-scoped
    /// handler (spec Edge Cases); a document whose IsLinked read throws
    /// is not safe to touch further.
    /// </summary>
    public static bool IsTrackable(Document? doc)
    {
        if (doc is null) return false;
        try { return !doc.IsLinked; }
        catch { return false; }
    }

    /// <summary>
    /// Stable per-session key for throttle + gate bookkeeping.
    /// CreationGUID survives Save / Save As, so the per-doc caps keep
    /// continuity across a re-path and a Save As can't fake an
    /// active-document change (SC-027); path/title are degraded
    /// fallbacks only. All cheap property reads — hot-path safe.
    /// </summary>
    public static string TrackingKey(Document doc) =>
        DocumentTrackingKey.Derive(
            Get(() => doc.CreationGUID.ToString()),
            Get(() => doc.PathName),
            Get(() => doc.Title));

    /// <summary>
    /// This open DOCUMENT, not its model: two open copies of one model
    /// (same CreationGUID, so the same <see cref="TrackingKey"/>) get
    /// different ids (rc.1 VM, F2). Revit's Document.GetHashCode is the
    /// same for every wrapper of one open document and is generated when
    /// the file is opened; the path keeps two documents apart even if two
    /// hash codes ever coincided (Revit never opens one path twice). A
    /// Save As changes the id, which costs nothing: a request made before
    /// it finds no document and is dropped, and the Save As requests its
    /// own capture. Cheap reads only; read only while the Document is
    /// valid (never on a closed document).
    /// </summary>
    public static string DocumentId(Document doc) =>
        (Get<int?>(() => doc.GetHashCode())?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?") +
        "|" + (Get(() => doc.PathName) ?? "");

    /// <summary>Join keys only — creation_guid + cloud pair +
    /// central_guid + central_path; the block every non-full-block doc
    /// event carries. The path is the file-share central's only central
    /// key (WorksharingCentralGUID is Revit Server-only), so it rides
    /// every keys-only event to keep close/sync endpoints matchable
    /// (SC-032, decision #3); cloud models keep it null.</summary>
    public static DocumentIdentity CaptureKeys(Document doc)
    {
        var identity = new DocumentIdentity
        {
            CreationGuid = Get(() => doc.CreationGUID.ToString()),
            IsCloud = Get<bool?>(() => doc.IsModelInCloud),
        };

        if (identity.IsCloud == true)
        {
            var cloudPath = Get(() => doc.GetCloudModelPath());
            if (cloudPath is not null)
            {
                identity.CloudProjectGuid = Get(() => cloudPath.GetProjectGUID().ToString());
                identity.CloudModelGuid = Get(() => cloudPath.GetModelGUID().ToString());
            }
        }

        identity.IsWorkshared = Get<bool?>(() => doc.IsWorkshared);
        if (identity.IsWorkshared == true && identity.IsCloud != true)
        {
            // File-based workshared only; throws on anything else → null,
            // so an unknown cloud-ness can't stamp a cloud value here.
            identity.CentralGuid = Get(() => doc.WorksharingCentralGUID.ToString());
            // The path CAN be wrongly stamped on a cloud model, so it
            // additionally requires known non-cloud (== false) — see
            // DocumentIdentity.AllowsCentralPath. Cheap property read +
            // in-memory path conversion — no file IO, hot-path safe.
            if (DocumentIdentity.AllowsCentralPath(identity.IsCloud))
            {
                identity.CentralPath = Get(() =>
                    ModelPathUtils.ConvertModelPathToUserVisiblePath(
                        doc.GetWorksharingCentralModelPath()));
            }
        }

        return identity;
    }

    /// <summary>
    /// The full identity block — doc_opened / doc_saved / doc_saved_as.
    /// Includes the one sanctioned file read: BasicFileInfo on
    /// doc.PathName, but only when the path verifies as local — a UNC
    /// or mapped-network path would turn the read into synchronous
    /// network IO inside a Revit event handler (SC-026). Non-local
    /// paths just lose version_guid/save_count; cloud models are keyed
    /// by their GUIDs anyway.
    /// </summary>
    public static DocumentIdentity CaptureFull(Document doc)
    {
        var identity = CaptureKeys(doc);

        identity.LocalPath = Get(() => doc.PathName);
        identity.Title = Get(() => doc.Title);
        identity.IsFamilyDoc = Get<bool?>(() => doc.IsFamilyDocument);
        identity.IsDetached = Get<bool?>(() => doc.IsDetached);

        if (LocalPathGuard.IsLocalFile(identity.LocalPath))
        {
            var version = Get(() =>
            {
                var info = BasicFileInfo.Extract(identity.LocalPath);
                return info.GetDocumentVersion();
            });
            if (version is not null)
            {
                identity.VersionGuid = Get(() => version.VersionGUID.ToString());
                identity.SaveCount = Get<int?>(() => version.NumberOfSaves);
            }
        }

        return identity;
    }

    private static T? Get<T>(Func<T?> read)
    {
        try { return read(); }
        catch { return default; }
    }
}
