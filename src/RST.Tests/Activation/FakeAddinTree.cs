// rst-c's own (not vendored): a fake machine for the add-in switching tests (spec #18, AC1–AC3):
// %AppData%, %ProgramData% and %ProgramFiles% under one temp folder, with helpers that write
// manifests, bundles (with PackageContents.xml), Add-Ins Manager settings and admin lock files the
// way Revit and the installers lay them out.

using System.IO;
using System.Text;
using RST.Core.Activation;

namespace RST.Tests.Activation;

public sealed class FakeAddinTree : IDisposable
{
    public FakeAddinTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "rst-addins-" + Guid.NewGuid().ToString("N"));
        Roots = new AddinRoots(Path.Combine(Root, "AppData"), Path.Combine(Root, "ProgramData"), Path.Combine(Root, "Program Files"));
        Directory.CreateDirectory(Roots.AppData);
        Directory.CreateDirectory(Roots.ProgramData);
        Directory.CreateDirectory(Roots.ProgramFiles);
    }

    public string Root { get; }

    public AddinRoots Roots { get; }

    public string LedgerPath => Path.Combine(Roots.AppData, "RST", ActivationLedger.FileName);

    public string UserAddins(int year) => Path.Combine(Roots.AppData, "Autodesk", "Revit", "Addins", Y(year));

    /// <summary>The machine Addins folder Revit reads for <paramref name="year"/> (Program Files from 2027).</summary>
    public string MachineAddins(int year) => year >= 2027
        ? Path.Combine(Roots.ProgramFiles, "Autodesk", "Revit", "Addins", Y(year))
        : Path.Combine(Roots.ProgramData, "Autodesk", "Revit", "Addins", Y(year));

    public string ProgramDataAddins(int year) => Path.Combine(Roots.ProgramData, "Autodesk", "Revit", "Addins", Y(year));

    public string UserPlugins => Path.Combine(Roots.AppData, "Autodesk", "ApplicationPlugins");

    public string ProgramDataPlugins => Path.Combine(Roots.ProgramData, "Autodesk", "ApplicationPlugins");

    public string ProgramFilesPlugins => Path.Combine(Roots.ProgramFiles, "Autodesk", "ApplicationPlugins");

    public string RevitInstallAddins(int year) => Path.Combine(Roots.DefaultRevitInstall(year), "AddIns");

    public static RevitYear Year(int year, SwitchMechanism mechanism = SwitchMechanism.SettingsFile) => new(year, null, null, mechanism);

    /// <summary>Writes a one-entry application manifest; returns its canonical (.addin) path.</summary>
    public string Manifest(string dir, string fileName, string name, string? id, string vendor = "ACME", bool renamed = false)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        var idLine = id is null ? "" : $"<ClientId>{id}</ClientId>";
        File.WriteAllText(renamed ? path + InventoryItem.RenamedSuffix : path,
            $"""<?xml version="1.0" encoding="utf-8"?><RevitAddIns><AddIn Type="Application"><Name>{name}</Name><Assembly>{name}.dll</Assembly><FullClassName>X.App</FullClassName>{idLine}<VendorId>{vendor}</VendorId></AddIn></RevitAddIns>""");
        return path;
    }

    public sealed record Component(string Folder, int? Min, int? Max, string FileName, string Name, string Id);

    /// <summary>Writes <c>&lt;root&gt;\&lt;bundle&gt;.bundle</c> with a PackageContents.xml naming each component.</summary>
    public string Bundle(string pluginsRoot, string bundleName, params Component[] components)
    {
        var dir = Path.Combine(pluginsRoot, bundleName + ".bundle");
        var xml = new StringBuilder("""<?xml version="1.0" encoding="utf-8"?><ApplicationPackage SchemaVersion="1.0" AutodeskProduct="Revit" ProductType="Application">""");
        foreach (var c in components)
        {
            Manifest(Path.Combine(dir, "Contents", c.Folder), c.FileName, c.Name, c.Id);
            var min = c.Min is null ? "" : $" SeriesMin=\"R{c.Min}\"";
            var max = c.Max is null ? "" : $" SeriesMax=\"R{c.Max}\"";
            xml.Append($"""<Components Description="{c.Folder}"><RuntimeRequirements OS="Win64" Platform="Revit"{min}{max} /><ComponentEntry AppName="{c.Name}" ModuleName="./Contents/{c.Folder}/{c.FileName}" /></Components>""");
        }
        xml.Append("</ApplicationPackage>");
        File.WriteAllText(Path.Combine(dir, "PackageContents.xml"), xml.ToString());
        return dir;
    }

    public void Settings(int year, string json)
    {
        var path = Roots.SettingsFile(year);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(json));
    }

    public string? ReadSettings(int year) => File.Exists(Roots.SettingsFile(year)) ? File.ReadAllText(Roots.SettingsFile(year)) : null;

    public byte[] SettingsBytes(int year) => File.ReadAllBytes(Roots.SettingsFile(year));

    public void Admin(int year, string json)
    {
        var path = Roots.AdminSettingsFile(year);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    /// <summary>An admin file locking <paramref name="id"/> for the manifest at <paramref name="manifestPath"/>.</summary>
    public void Lock(int year, string manifestPath, string id, AdminLock option) =>
        Admin(year, "{\"DisableAllAddIns\":false,\"AddInManifestAdminSettings\":[{\"Path\":" + System.Text.Json.JsonSerializer.Serialize(manifestPath)
                    + ",\"AddInItemAdminSettings\":[{\"AddInId\":\"" + id + "\",\"LoadOption\":" + (int)option + "}]}]}");

    public AddinInventory Scan(params RevitYear[] years) => AddinInventory.Scan(Roots, years);

    public AddinInventory Scan(params int[] years) => Scan(years.Select(y => Year(y)).ToArray());

    public ActivationExecutor Executor(IActivationFileSystem? fs = null) =>
        new(Roots, LedgerPath, fs, () => new DateTime(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc));

    public ActivationLedger Ledger() => ActivationLedger.Load(LedgerPath);

    /// <summary>Scan, plan with the ledger on disk, execute: what the Loader does.</summary>
    public (ActivationPlan Plan, ActivationReport Report) Run(ActivationPolicy policy, IActivationFileSystem? fs = null, params RevitYear[] years)
    {
        var plan = ActivationPlanner.Plan(Scan(years), policy, Ledger());
        return (plan, Executor(fs).Execute(plan));
    }

    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AddinsManager", name));

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Y(int year) => year.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The real file system, with failures scripted per path (the way Windows fails them).</summary>
public sealed class FailingFileSystem : IActivationFileSystem
{
    private readonly IActivationFileSystem _inner = ActivationFileSystem.Instance;

    /// <summary>Thrown by a Move whose source is the key.</summary>
    public Dictionary<string, Exception> MoveThrows { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Thrown by a WriteAtomic to the key.</summary>
    public Dictionary<string, Exception> WriteThrows { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Writes { get; } = [];

    public static IOException SharingViolation() => new("The process cannot access the file because it is being used by another process.", unchecked((int)0x80070020));

    public bool FileExists(string path) => _inner.FileExists(path);

    public string ReadAllText(string path) => _inner.ReadAllText(path);

    public void WriteAtomic(string path, byte[] bytes)
    {
        if (WriteThrows.TryGetValue(path, out var ex)) throw ex;
        Writes.Add(path);
        _inner.WriteAtomic(path, bytes);
    }

    public void Move(string source, string destination)
    {
        if (MoveThrows.TryGetValue(source, out var ex)) throw ex;
        _inner.Move(source, destination);
    }

    public void Copy(string source, string destination) => _inner.Copy(source, destination);
}
