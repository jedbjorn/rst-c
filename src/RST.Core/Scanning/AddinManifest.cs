// AddinManifest.cs — parsed representation of a Revit .addin XML file.
//
// A single .addin file may declare multiple <AddIn> elements (Application,
// Command, DBApplication, etc.), each with its own Assembly + AddInId.

using System.Collections.Generic;

namespace RST.Core.Scanning;

public sealed record AddinManifest(
    string FilePath,
    string FileName,
    bool IsDisabled,
    IReadOnlyList<AddinEntry> Entries);

public sealed record AddinEntry(
    string Type,
    string? AssemblyPath,
    string? AddinId,
    string? Name,
    string? VendorId,
    string? VendorDescription);
