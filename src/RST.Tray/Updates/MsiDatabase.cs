// rst-c's own (not vendored): reads a downloaded MSI's Property table (spec #20 §5 check 3; F17 WU4), the same
// fields companions/README.md C1 checks by hand: UpgradeCode, ProductVersion, ProductCode, ALLUSERS and
// MSIINSTALLPERUSER. P/Invoke to msi.dll, loaded from System32 only:
//
//   MsiOpenDatabaseW(path, MSIDBOPEN_READONLY)   then   SELECT `Property`, `Value` FROM `Property`
//
// MSIHANDLE is a 32-bit unsigned long on every platform. Every handle is closed before Read returns, so msiexec
// never finds the file locked. Any failure throws: VerifiedDownload refuses the MSI as unreadable.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using RST.Core.Updates;

namespace RST.Tray.Updates;

internal sealed class MsiDatabase : IMsiReader
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorMoreData = 234;
    private const uint ErrorNoMoreItems = 259;
    private static readonly IntPtr ReadOnly = IntPtr.Zero; // MSIDBOPEN_READONLY
    private static readonly IntPtr Transact = new(1);      // MSIDBOPEN_TRANSACT

    public MsiIdentity Read(string msiPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(msiPath);
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        Check(MsiOpenDatabaseW(msiPath, ReadOnly, out var db), "MsiOpenDatabase");
        try
        {
            Check(MsiDatabaseOpenViewW(db, "SELECT `Property`, `Value` FROM `Property`", out var view), "MsiDatabaseOpenView");
            try
            {
                Check(MsiViewExecute(view, 0), "MsiViewExecute");
                while (true)
                {
                    var fetched = MsiViewFetch(view, out var record);
                    if (fetched == ErrorNoMoreItems) break;
                    Check(fetched, "MsiViewFetch");
                    try
                    {
                        properties[Field(record, 1)] = Field(record, 2);
                    }
                    finally
                    {
                        MsiCloseHandle(record);
                    }
                }
            }
            finally
            {
                MsiCloseHandle(view);
            }
        }
        finally
        {
            MsiCloseHandle(db);
        }
        string? Get(string name) => properties.TryGetValue(name, out var value) ? value : null;
        return new MsiIdentity(Get("UpgradeCode"), Get("ProductVersion"), Get("ProductCode"), Get("ALLUSERS"), Get("MSIINSTALLPERUSER"));
    }

    /// <summary>Runs SQL statements on a copy of an MSI and commits them (RuntimeTests build their wrong-identity variants with it).</summary>
    internal static void Execute(string msiPath, params string[] statements)
    {
        Check(MsiOpenDatabaseW(msiPath, Transact, out var db), "MsiOpenDatabase (transact)");
        try
        {
            foreach (var sql in statements)
            {
                Check(MsiDatabaseOpenViewW(db, sql, out var view), "MsiDatabaseOpenView: " + sql);
                try
                {
                    Check(MsiViewExecute(view, 0), "MsiViewExecute: " + sql);
                }
                finally
                {
                    MsiCloseHandle(view);
                }
            }
            Check(MsiDatabaseCommit(db), "MsiDatabaseCommit");
        }
        finally
        {
            MsiCloseHandle(db);
        }
    }

    private static string Field(uint record, uint field)
    {
        uint size = 64;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = new StringBuilder((int)size + 1);
            var length = size + 1;
            var result = MsiRecordGetStringW(record, field, buffer, ref length);
            if (result == ErrorSuccess) return buffer.ToString();
            if (result != ErrorMoreData) Check(result, "MsiRecordGetString");
            size = length + 1;
        }
        throw new InvalidOperationException("MsiRecordGetString: the value kept growing");
    }

    private static void Check(uint result, string call)
    {
        if (result != ErrorSuccess) throw new InvalidOperationException($"{call} returned {result} ({new Win32Exception((int)result).Message})");
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint MsiOpenDatabaseW(string szDatabasePath, IntPtr szPersist, out uint phDatabase);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint MsiDatabaseOpenViewW(uint hDatabase, string szQuery, out uint phView);

    [DllImport("msi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint MsiViewExecute(uint hView, uint hRecord);

    [DllImport("msi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint MsiViewFetch(uint hView, out uint phRecord);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint MsiRecordGetStringW(uint hRecord, uint iField, StringBuilder szValueBuf, ref uint pcchValueBuf);

    [DllImport("msi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint MsiDatabaseCommit(uint hDatabase);

    [DllImport("msi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint MsiCloseHandle(uint hAny);
}
