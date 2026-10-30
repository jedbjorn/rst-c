// rst-c's own (not vendored): the file operations the executor performs, behind one seam so the
// tests can make a write fail the way Windows does (access denied, sharing violation, a file that
// vanished) without real ACLs (spec #18, AC3).

namespace RST.Core.Activation;

public interface IActivationFileSystem
{
    bool FileExists(string path);

    string ReadAllText(string path);

    /// <summary>Writes <paramref name="bytes"/> to <paramref name="path"/> atomically: a temp file next to it, then a replace.</summary>
    void WriteAtomic(string path, byte[] bytes);

    /// <summary>Renames a file; throws <see cref="IOException"/> when the destination exists.</summary>
    void Move(string source, string destination);

    /// <summary>Copies a file, overwriting.</summary>
    void Copy(string source, string destination);
}

/// <summary>The real file system.</summary>
public sealed class ActivationFileSystem : IActivationFileSystem
{
    public static readonly ActivationFileSystem Instance = new();

    public const string TempSuffix = ".rst.tmp";

    public bool FileExists(string path) => File.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAtomic(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + TempSuffix;
        try
        {
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Move(string source, string destination) => File.Move(source, destination, overwrite: false);

    public void Copy(string source, string destination) => File.Copy(source, destination, overwrite: true);
}
