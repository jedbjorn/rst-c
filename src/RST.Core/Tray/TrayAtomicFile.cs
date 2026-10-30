// Copied from splash-it src/SplashIt.Core/Settings/AtomicFile.cs (splash-it commit ba86a71), MIT, same owner.
// Changes: namespace (SplashIt.Core.Settings to RST.Core.Tray); named TrayAtomicFile; its log lines say "tray settings".

using System;
using System.IO;
using System.Text;
using System.Threading;

namespace RST.Core.Tray;

/// <summary>
/// Writes a file so a reader never sees it half-written, and so the target is never
/// left missing: the text goes to a temp file in the same folder, is flushed to disk,
/// then takes the target's place.
/// <list type="number">
/// <item>The swap is <see cref="File.Move(string, string, bool)"/> with overwrite: one
/// rename (MoveFileEx with MOVEFILE_REPLACE_EXISTING on Windows, rename(2) elsewhere)
/// that never leaves the target missing.</item>
/// <item>Windows refuses that rename while any other handle has the target open, even
/// one opened with <see cref="FileShare.Delete"/> (a reader of ours, an antivirus scanner,
/// a sync client). Only then, and only when the target exists, the swap falls back to
/// <see cref="File.Replace(string, string, string?)"/> (Win32 ReplaceFile), which does work
/// over a reader that shares delete.</item>
/// <item>ReplaceFile can fail with ERROR_UNABLE_TO_MOVE_REPLACEMENT (1176) after it has
/// already removed the target. Whenever a ReplaceFile call fails and the target is gone,
/// the temp file is moved into place at once (nothing holds the name any more).</item>
/// <item>The whole swap is retried a few times, briefly, each failure logged; then the
/// error surfaces with the previous file kept.</item>
/// <item>The temp file is deleted at the end only while the target exists. In the one
/// case where every attempt failed and the target is gone, the temp file holding the new
/// contents is kept (and logged) rather than deleted.</item>
/// </list>
/// Readers of files written here should open them with
/// <c>FileShare.ReadWrite | FileShare.Delete</c> (<see cref="ReadAllText"/> does).
/// </summary>
internal static class TrayAtomicFile
{
    /// <summary>How many times the swap is tried.</summary>
    internal const int ReplaceAttempts = 5;

    /// <summary>The wait before the first retry; it doubles each time (about 0.3 s in all).</summary>
    internal const int FirstRetryDelayMs = 20;

    /// <summary>The file-system calls the swap makes; tests substitute them to reproduce Windows failures.</summary>
    internal class FileOps
    {
        public virtual void Move(string source, string destination, bool overwrite) =>
            File.Move(source, destination, overwrite);

        public virtual void Replace(string source, string destination) =>
            File.Replace(source, destination, destinationBackupFileName: null);
    }

    private static readonly FileOps RealOps = new();

    /// <summary>Test seam, scoped to the calling async flow so parallel tests are unaffected.</summary>
    internal static readonly AsyncLocal<FileOps?> OpsOverride = new();

    private static FileOps Ops => OpsOverride.Value ?? RealOps;

    public static void WriteAllText(string path, string contents, Action<string>? log = null)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)
            ?? throw new ArgumentException($"'{path}' has no parent folder.", nameof(path));
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            ReplaceWithRetry(temp, full, log);
        }
        finally
        {
            if (Path.Exists(full) || !File.Exists(temp))
                TryDelete(temp);
            else
                log?.Invoke(FormattableString.Invariant(
                    $"tray settings: {Path.GetFileName(full)} is missing; its new contents are kept in {Path.GetFileName(temp)}."));
        }
    }

    /// <summary>
    /// Reads a file written by <see cref="WriteAllText"/> without blocking a concurrent
    /// replace: it opens with <c>FileShare.ReadWrite | FileShare.Delete</c>, and retries a
    /// sharing failure briefly (a writer on Windows may hold the name for an instant).
    /// Throws what the last attempt threw.
    /// </summary>
    public static string ReadAllText(string path)
    {
        var delay = FirstRetryDelayMs;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException)
                                       && ex is not FileNotFoundException and not DirectoryNotFoundException
                                       && attempt < ReplaceAttempts)
            {
                Thread.Sleep(delay);
                delay *= 2;
            }
        }
    }

    private static void ReplaceWithRetry(string temp, string target, Action<string>? log)
    {
        var delay = FirstRetryDelayMs;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                SwapOnce(temp, target, log);
                if (attempt > 1)
                    log?.Invoke(FormattableString.Invariant($"tray settings: replaced {Path.GetFileName(target)} on attempt {attempt}."));
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= ReplaceAttempts)
                {
                    log?.Invoke(Path.Exists(target)
                        ? FormattableString.Invariant(
                            $"tray settings: replacing {Path.GetFileName(target)} failed ({ex.GetType().Name}) after {ReplaceAttempts} attempts; the previous file is kept.")
                        : FormattableString.Invariant(
                            $"tray settings: replacing {Path.GetFileName(target)} failed ({ex.GetType().Name}) after {ReplaceAttempts} attempts."));
                    throw;
                }
                log?.Invoke(FormattableString.Invariant(
                    $"tray settings: replacing {Path.GetFileName(target)} failed ({ex.GetType().Name}), attempt {attempt} of {ReplaceAttempts}; retrying in {delay} ms."));
                Thread.Sleep(delay);
                delay *= 2;
            }
        }
    }

    /// <summary>One swap: a move with overwrite, else (target held open on Windows) ReplaceFile, never leaving the target missing.</summary>
    private static void SwapOnce(string temp, string target, Action<string>? log)
    {
        var ops = Ops;
        try
        {
            ops.Move(temp, target, overwrite: true);
            return;
        }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && File.Exists(target))
        {
            // Refused because another handle has the target open; ReplaceFile copes with
            // a handle that shares delete. Fall through.
        }

        try
        {
            ops.Replace(temp, target);
        }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException)
                                   && !Path.Exists(target) && File.Exists(temp))
        {
            // ERROR_UNABLE_TO_MOVE_REPLACEMENT (1176): ReplaceFile removed the target, then
            // could not rename the temp file. Put it in place now; nothing holds the name.
            // If this move fails too, the retry loop tries again and the temp file is kept.
            ops.Move(temp, target, overwrite: false);
            log?.Invoke(FormattableString.Invariant(
                $"tray settings: ReplaceFile failed after removing {Path.GetFileName(target)} ({ex.GetType().Name}); moved the new file into place."));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover dot-prefixed temp file is harmless.
        }
    }
}
