// rst-c's own (not vendored): where the bootstrap loads the engine from (feature #17, spec #20 §7; F17 WU5).

using System.IO; // not in WPF's implicit usings
using RST.Bootstrap;
using Xunit;

namespace RST.RuntimeTests.Bootstrap;

/// <summary>
/// <see cref="EngineLocation"/> for both install layouts, on real folders: RST-Machine.msi's
/// (<c>Program Files\RST\R&lt;NN&gt;\RST.Bootstrap.dll</c> with the engine in <c>app\</c> beside it) and RST.msi's
/// (the bootstrap in the per-user Add-Ins folder, the engine in <c>%AppData%\RST\R&lt;NN&gt;\app\</c>).
/// </summary>
public sealed class EngineLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rst-engine-location-" + Guid.NewGuid().ToString("N"));

    public EngineLocationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string AppData => Path.Combine(_root, "Roaming");

    [Fact]
    public void Per_machine_layout_loads_the_engine_beside_the_bootstrap()
    {
        var bootstrapDir = Path.Combine(_root, "Program Files", "RST", "R26");
        Directory.CreateDirectory(Path.Combine(bootstrapDir, "app"));
        // A per-user engine of the same major is present too: the bootstrap's own layout wins.
        Directory.CreateDirectory(Path.Combine(AppData, "RST", "R26", "app"));

        var (appDir, layout) = EngineLocation.Resolve(bootstrapDir, AppData, "R26", Directory.Exists);

        Assert.Equal(Path.Combine(bootstrapDir, "app"), appDir);
        Assert.Equal(EngineLocation.BesideBootstrap, layout);
    }

    [Fact]
    public void Per_user_layout_loads_the_engine_from_AppData()
    {
        var bootstrapDir = Path.Combine(AppData, "Autodesk", "Revit", "Addins", "2027");
        Directory.CreateDirectory(bootstrapDir);
        Directory.CreateDirectory(Path.Combine(AppData, "RST", "R27", "app"));

        var (appDir, layout) = EngineLocation.Resolve(bootstrapDir, AppData, "R27", Directory.Exists);

        Assert.Equal(Path.Combine(AppData, "RST", "R27", "app"), appDir);
        Assert.Equal(EngineLocation.PerUser, layout);
    }

    [Fact]
    public void A_file_named_app_beside_the_bootstrap_is_not_an_engine_folder()
    {
        var bootstrapDir = Path.Combine(AppData, "Autodesk", "Revit", "Addins", "2025");
        Directory.CreateDirectory(bootstrapDir);
        File.WriteAllText(Path.Combine(bootstrapDir, "app"), "not a folder");

        var (appDir, layout) = EngineLocation.Resolve(bootstrapDir, AppData, "R25", Directory.Exists);

        Assert.Equal(Path.Combine(AppData, "RST", "R25", "app"), appDir);
        Assert.Equal(EngineLocation.PerUser, layout);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void No_bootstrap_folder_falls_back_to_the_per_user_engine(string? bootstrapDir)
    {
        var probed = new List<string>();

        var (appDir, layout) = EngineLocation.Resolve(bootstrapDir, AppData, "R26", p => { probed.Add(p); return true; });

        Assert.Equal(Path.Combine(AppData, "RST", "R26", "app"), appDir);
        Assert.Equal(EngineLocation.PerUser, layout);
        Assert.Empty(probed);
    }

    [Fact]
    public void The_per_user_engine_path_is_returned_even_when_missing_so_the_bootstrap_logs_it()
    {
        var bootstrapDir = Path.Combine(AppData, "Autodesk", "Revit", "Addins", "2026");

        var (appDir, _) = EngineLocation.Resolve(bootstrapDir, AppData, "R26", Directory.Exists);

        Assert.Equal(Path.Combine(AppData, "RST", "R26", "app"), appDir);
        Assert.False(Directory.Exists(appDir));
    }
}
