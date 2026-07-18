// LocalPathGuard.cs — decides whether a document path is safe for the
// collector's one sanctioned file read (BasicFileInfo on doc_opened /
// doc_saved / doc_saved_as).
//
// Spec Threading & Safety: handlers never do network IO. A Revit
// PathName can be a UNC share, a mapped network drive, or a cloud
// pseudo-path — reading a file header on any of those from an event
// handler would do synchronous network IO on the UI thread. Only a path
// on a verified-local drive passes; everything unverifiable fails
// closed (the caller merely loses version_guid/save_count). The
// drive-type probe (GetDriveType, via DriveInfo) is itself a local
// metadata lookup — it never touches the network — and is injectable
// because tests cannot mint network drives in CI.

using System.Diagnostics.CodeAnalysis;

namespace RST.Core.Telemetry;

public static class LocalPathGuard
{
    /// <summary>True only for a rooted drive-letter path whose drive
    /// probes as a local type (fixed / removable / RAM / optical).</summary>
    public static bool IsLocalFile([NotNullWhen(true)] string? path) =>
        IsLocalFile(path, ProbeDriveType);

    internal static bool IsLocalFile(
        [NotNullWhen(true)] string? path, Func<char, DriveType?> driveTypeProbe)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        // UNC — \\server\share (or the forward-slash spelling).
        if (path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]))
            return false;

        // Cloud pseudo-paths ("BIM 360://…", "Autodesk Docs://…").
        if (path.Contains("://", StringComparison.Ordinal)) return false;

        // Anything but a rooted drive-letter path is unverifiable → fail
        // closed. (Production is Windows; local files always carry X:\.)
        if (path.Length < 3
            || !char.IsAsciiLetter(path[0])
            || path[1] != ':'
            || !IsSeparator(path[2]))
        {
            return false;
        }

        // Unknown / NoRootDirectory / Network / probe failure all fail:
        // "not provably local" and "remote" get the same answer.
        return driveTypeProbe(path[0])
            is DriveType.Fixed or DriveType.Removable or DriveType.Ram or DriveType.CDRom;
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';

    private static DriveType? ProbeDriveType(char driveLetter)
    {
        try { return new DriveInfo(driveLetter + ":\\").DriveType; }
        catch { return null; }
    }
}
