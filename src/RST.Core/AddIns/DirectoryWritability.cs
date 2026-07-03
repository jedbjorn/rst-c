// DirectoryWritability.cs — live write probe for add-in search paths.
//
// The disable step renames .addin files in place, so a search path is
// only actionable when the RUNNING process token can write to it. ACL
// inspection can't answer that reliably (group membership, UAC filtered
// tokens, inherited deny entries), so we ask the filesystem directly:
// create a uniquely-named temp file and let the OS delete it on close.
//
// The distinction matters because the same directory answers differently
// per token: an elevated console session can rename ProgramData manifests
// while interactive (non-elevated) Revit gets UnauthorizedAccessException
// on the very same files. Classifying by path kind alone made the confirm
// modal promise disables the commit could never deliver.
//
// Approximation note: a rename additionally needs DELETE on the target
// file (or FILE_DELETE_CHILD on the directory); this probe tests file
// creation. In practice the two travel together on the Autodesk add-in
// directories, and the commit path still catches and reports any rename
// that fails despite a passing probe.

using System;
using System.IO;

namespace RST.Core.AddIns;

public static class DirectoryWritability
{
    /// <summary>
    /// True when the current process token can create a file in
    /// <paramref name="directory"/>. Any failure — missing directory,
    /// access denied, IO error — reports not-writable.
    /// </summary>
    public static bool CanWrite(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        var probePath = Path.Combine(directory, ".rst-write-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            // DeleteOnClose: the OS removes the probe when the handle
            // closes, even if the process dies mid-probe.
            using var _ = new FileStream(
                probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
