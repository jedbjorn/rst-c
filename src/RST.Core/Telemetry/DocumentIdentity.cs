// DocumentIdentity.cs — the raw model-identity block every doc-scoped
// event carries, null where not applicable.
//
// Captured raw on the client; linkage resolution (cloud GUIDs → central
// GUID/path → creation-GUID lineage) is a server concern. The collector
// (RST.Engine) populates this from Revit API reads; this type only knows
// the wire fields, keeping RST.Core free of Revit references.
//
// Two write shapes per the spec's capture points:
//   full block  — doc_opened, doc_saved, doc_saved_as
//   keys only   — every other doc event (creation_guid + cloud/central
//                 keys, enough to join)

namespace RST.Core.Telemetry;

public sealed class DocumentIdentity
{
    public string? CreationGuid { get; set; }
    public string? VersionGuid { get; set; }
    public int? SaveCount { get; set; }
    public string? CloudProjectGuid { get; set; }
    public string? CloudModelGuid { get; set; }
    public string? CentralGuid { get; set; }
    public string? CentralPath { get; set; }
    public string? LocalPath { get; set; }
    public string? Title { get; set; }
    public bool? IsWorkshared { get; set; }
    public bool? IsCloud { get; set; }
    public bool? IsFamilyDoc { get; set; }
    public bool? IsDetached { get; set; }

    /// <summary>
    /// Best local handle for "same document" bookkeeping (open-doc
    /// tracking in the recovery scanner, throttle keys in the collector).
    /// NOT server resolution — just a stable per-session key.
    /// </summary>
    public string? JoinKey =>
        CloudModelGuid ?? CentralGuid ?? CreationGuid ?? LocalPath ?? Title;

    /// <summary>Write the full block — nulls included, absence is data.</summary>
    public void WriteTo(TelemetryEvent e)
    {
        WriteKeysTo(e);
        e.SetField(TelemetryFields.VersionGuid, VersionGuid);
        e.SetField(TelemetryFields.SaveCount, SaveCount);
        e.SetField(TelemetryFields.CentralPath, CentralPath);
        e.SetField(TelemetryFields.LocalPath, LocalPath);
        e.SetField(TelemetryFields.Title, Title);
        e.SetField(TelemetryFields.IsWorkshared, IsWorkshared);
        e.SetField(TelemetryFields.IsCloud, IsCloud);
        e.SetField(TelemetryFields.IsFamilyDoc, IsFamilyDoc);
        e.SetField(TelemetryFields.IsDetached, IsDetached);
    }

    /// <summary>Write the join keys only (creation_guid + cloud/central keys).</summary>
    public void WriteKeysTo(TelemetryEvent e)
    {
        e.SetField(TelemetryFields.CreationGuid, CreationGuid);
        e.SetField(TelemetryFields.CloudProjectGuid, CloudProjectGuid);
        e.SetField(TelemetryFields.CloudModelGuid, CloudModelGuid);
        e.SetField(TelemetryFields.CentralGuid, CentralGuid);
    }

    public static DocumentIdentity ReadFrom(TelemetryEvent e) => new()
    {
        CreationGuid = e.GetString(TelemetryFields.CreationGuid),
        VersionGuid = e.GetString(TelemetryFields.VersionGuid),
        SaveCount = (int?)e.GetInt64(TelemetryFields.SaveCount),
        CloudProjectGuid = e.GetString(TelemetryFields.CloudProjectGuid),
        CloudModelGuid = e.GetString(TelemetryFields.CloudModelGuid),
        CentralGuid = e.GetString(TelemetryFields.CentralGuid),
        CentralPath = e.GetString(TelemetryFields.CentralPath),
        LocalPath = e.GetString(TelemetryFields.LocalPath),
        Title = e.GetString(TelemetryFields.Title),
        IsWorkshared = e.GetBool(TelemetryFields.IsWorkshared),
        IsCloud = e.GetBool(TelemetryFields.IsCloud),
        IsFamilyDoc = e.GetBool(TelemetryFields.IsFamilyDoc),
        IsDetached = e.GetBool(TelemetryFields.IsDetached),
    };
}
