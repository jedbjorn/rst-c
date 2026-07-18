// IdentityCapture.cs — maps a Revit Document to the raw DocumentIdentity
// wire block (spec Model Identity). Capture raw, resolve nothing: the
// cloud-GUID → central-GUID → creation-GUID priority is a server concern.
//
// Every field read wraps in its own guard → null: an identity gap is
// data, never an exception surfaced to Revit. All reads are cheap
// property reads except BasicFileInfo.Extract, which reads one local
// file header and therefore only runs for the full block (doc_opened /
// doc_saved / doc_saved_as) — never on the keys-only hot paths.

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
    /// Cheap per-session key for throttle + gate bookkeeping — NOT
    /// identity (Save As re-keys, which merely costs one extra pulse).
    /// </summary>
    public static string TrackingKey(Document doc)
    {
        var path = Get(() => doc.PathName);
        if (!string.IsNullOrEmpty(path)) return path!;
        return Get(() => doc.Title) ?? "untitled";
    }

    /// <summary>Join keys only — creation_guid + cloud/central GUIDs;
    /// the block every non-full-block doc event carries.</summary>
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
            // File-based workshared only; throws on anything else → null.
            identity.CentralGuid = Get(() => doc.WorksharingCentralGUID.ToString());
        }

        return identity;
    }

    /// <summary>
    /// The full identity block — doc_opened / doc_saved / doc_saved_as.
    /// Includes the one sanctioned file read: BasicFileInfo on
    /// doc.PathName (a share-hosted path costs one guarded read that
    /// fails to nulls; a cloud path simply fails to nulls, the cloud
    /// GUIDs suffice).
    /// </summary>
    public static DocumentIdentity CaptureFull(Document doc)
    {
        var identity = CaptureKeys(doc);

        identity.LocalPath = Get(() => doc.PathName);
        identity.Title = Get(() => doc.Title);
        identity.IsFamilyDoc = Get<bool?>(() => doc.IsFamilyDocument);
        identity.IsDetached = Get<bool?>(() => doc.IsDetached);

        if (identity.IsWorkshared == true && identity.IsCloud != true)
        {
            identity.CentralPath = Get(() =>
                ModelPathUtils.ConvertModelPathToUserVisiblePath(
                    doc.GetWorksharingCentralModelPath()));
        }

        if (!string.IsNullOrEmpty(identity.LocalPath))
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
