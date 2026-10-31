// rst-c's own (not vendored): how cleanup takes a raw outbox file before
// deleting it (spec #24, Cleanup; PR #177 review R2). The file is held
// exclusively, verified through that handle (length, last write, content),
// and deleted through the same handle, so what is deleted is exactly what
// was verified — never a file that replaced it at the same path.
//
//   Windows  CreateFile with GENERIC_READ | DELETE and no sharing (a live
//            Revit writer refuses it; nobody can write, rename or replace
//            the file while it is held), then FileDispositionInfo on the
//            handle: the held file object is deleted when the handle closes.
//   others   (CI on Linux, no production use) an exclusive open, then the
//            path is renamed aside, compared with the held handle and only
//            then unlinked; a mismatch (the path was replaced) is put back.

using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RST.Core.Telemetry.Rollups;

/// <summary>A raw outbox file held exclusively for cleanup.</summary>
internal interface ITakenFile : IDisposable
{
    long Length { get; }
    DateTimeOffset LastWriteUtc { get; }

    /// <summary>The file's content, read through the held handle.</summary>
    byte[] ReadAll();

    /// <summary>Delete the held file — the one verified, not whatever is at the path now.
    /// False when it could not be (it stays).</summary>
    bool Delete();
}

/// <summary>The seam cleanup takes files through (tests substitute one).</summary>
internal interface IFileTaker
{
    /// <summary>The file held exclusively; null when it cannot be (live, locked or gone).</summary>
    ITakenFile? TryTake(string path);
}

internal sealed class OutboxFileTaker : IFileTaker
{
    public static readonly OutboxFileTaker Default = new();

    public ITakenFile? TryTake(string path)
    {
        try
        {
            return OperatingSystem.IsWindows() ? WindowsTaken.Open(path) : PortableTaken.Open(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private abstract class Taken : ITakenFile
    {
        protected Taken(string path, SafeFileHandle handle)
        {
            Path = path;
            Handle = handle;
        }

        protected string Path { get; }
        protected SafeFileHandle Handle { get; }

        public long Length => RandomAccess.GetLength(Handle);

        public DateTimeOffset LastWriteUtc =>
            RollupJson.TruncateMs(new DateTimeOffset(File.GetLastWriteTimeUtc(Handle), TimeSpan.Zero));

        public byte[] ReadAll()
        {
            var buffer = new byte[Length];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = RandomAccess.Read(Handle, buffer.AsSpan(read), read);
                if (n <= 0) break;
                read += n;
            }
            return read == buffer.Length ? buffer : buffer.AsSpan(0, read).ToArray();
        }

        public abstract bool Delete();

        public void Dispose() => Handle.Dispose();
    }

    private sealed class WindowsTaken : Taken
    {
        private const uint GenericRead = 0x80000000;
        private const uint DeleteAccess = 0x00010000;
        private const uint OpenExisting = 3;
        private const uint FileAttributeNormal = 0x80;
        private const int FileDispositionInfoClass = 4;

        private WindowsTaken(string path, SafeFileHandle handle) : base(path, handle) { }

        public static WindowsTaken Open(string path)
        {
            var h = CreateFileW(path, GenericRead | DeleteAccess, 0, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
            if (h.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                h.Dispose();
                throw new IOException("cannot take " + System.IO.Path.GetFileName(path) + " (error " + error + ")");
            }
            return new WindowsTaken(path, h);
        }

        public override bool Delete()
        {
            var info = new FileDispositionInfo { DeleteFile = 1 };
            if (!SetFileInformationByHandle(Handle, FileDispositionInfoClass, ref info, 1)) return false;
            Handle.Dispose(); // the held file object goes when its last handle closes
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInfo
        {
            public byte DeleteFile;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle hFile, int fileInformationClass,
            ref FileDispositionInfo lpFileInformation, uint dwBufferSize);
    }

    private sealed class PortableTaken : Taken
    {
        private PortableTaken(string path, SafeFileHandle handle) : base(path, handle) { }

        public static PortableTaken Open(string path) =>
            new(path, File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.None));

        public override bool Delete()
        {
            // No delete-by-handle here: move the path aside atomically, and unlink it only
            // when it is still the held file; a replacement goes back where it was.
            var aside = Path + ".deleting";
            try { File.Move(Path, aside, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
            var fi = new FileInfo(aside);
            var same = fi.Length == Length &&
                RollupJson.TruncateMs(new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero)) == LastWriteUtc;
            if (same)
            {
                File.Delete(aside);
                return true;
            }
            if (!File.Exists(Path)) File.Move(aside, Path);
            return false;
        }
    }
}
