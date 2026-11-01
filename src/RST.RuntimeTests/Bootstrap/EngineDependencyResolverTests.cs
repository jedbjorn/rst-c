// rst-c's own (not vendored): the bootstrap's assembly resolver (decisions #27, #32 and #33, research M3).

using System.IO; // not in WPF's implicit usings
using System.Reflection;
using System.Runtime.Loader;
using RST.Bootstrap;
using Xunit;

namespace RST.RuntimeTests.Bootstrap;

/// <summary>
/// Every branch of <see cref="EngineDependencyResolver"/>, the bootstrap's process-wide
/// <c>AssemblyLoadContext.Default.Resolving</c> handler: a third-party name (never answered,
/// decision #33), not ours, nothing loaded and requested newer than shipped, a compatible copy
/// already loaded, an older same-major copy already loaded (handed over with one warning per
/// session, decision #32), an older different-major copy (refused), a fresh load by path, a request
/// without a version, and every failure (it must never throw from inside the event). Plus the
/// startup "Third-party DLLs shipped" line, the requester lookup, and the production wiring over a
/// real deps.json resolver. The loaded assemblies the branches use are RST.Core and RST.UI, which
/// this test process has loaded.
/// </summary>
public class EngineDependencyResolverTests
{
    private const string CorePath = @"C:\Users\u\AppData\Roaming\RST\R26\app\RST.Core.dll";

    /// <summary>A loaded, non-dynamic RST assembly whose version and path the tests can read.</summary>
    private static readonly Assembly Loaded = typeof(RST.Core.Logging.Log).Assembly; // RST.Core

    private static Version LoadedVersion => Loaded.GetName().Version!;

    private sealed class Harness
    {
        public List<string> Info { get; } = new();
        public List<string> Warn { get; } = new();
        public List<string> LoadedPaths { get; } = new();

        public Func<AssemblyName, string?> PathFor { get; set; } = _ => CorePath;
        public Func<string, Version?> VersionAt { get; set; } = _ => new Version(4, 0, 0, 0);
        public Func<IEnumerable<Assembly>> InContext { get; set; } = () => Array.Empty<Assembly>();
        public Func<string, Assembly>? Load { get; set; }
        public Func<string?> Requester { get; set; } = () => "ZZZ-SerilogProbe";

        /// <summary>What a load by path returns (any real assembly; the tests check identity).</summary>
        public Assembly LoadResult { get; } = typeof(object).Assembly;

        public EngineDependencyResolver Build() => new(
            n => PathFor(n),
            p => VersionAt(p),
            () => InContext(),
            p =>
            {
                LoadedPaths.Add(p);
                return Load is null ? LoadResult : Load(p);
            },
            Info.Add,
            Warn.Add,
            () => Requester());
    }

    /// <summary>A version above the loaded one with the same major (a 0.1 loaded, 0.3 asked).</summary>
    private static Version SameMajorNewer => new(LoadedVersion.Major, LoadedVersion.Minor + 2, 0, 0);

    private static AssemblyName Name(string name, string? version) =>
        new(version is null ? name : $"{name}, Version={version}");

    [Theory]
    [InlineData("Serilog")]
    [InlineData("Serilog.Sinks.File")]
    [InlineData("System.Management")]
    [InlineData("System.Text.Json")]
    [InlineData("Newtonsoft.Json")]
    [InlineData("RSTX")] // the prefix without its dot is not RST's
    [InlineData("AAA-SerilogProbe")]
    public void ThirdPartyName_IsNeverAnswered_EvenWhenTheDepsJsonWouldResolveIt(string name)
    {
        // Decision #33: RST ships no third-party DLL, so a request for one is another add-in's
        // business. Declined before the deps.json is asked, without loading or logging.
        var asked = 0;
        var h = new Harness
        {
            PathFor = _ =>
            {
                asked++;
                return @"C:\Users\u\AppData\Roaming\RST\R26\app\" + name + ".dll";
            },
            InContext = () => new[] { Loaded },
        };

        Assert.Null(h.Build().Resolve(Name(name, "4.0.0.0")));
        Assert.Equal(0, asked);
        Assert.Empty(h.LoadedPaths);
        Assert.Empty(h.Info);
        Assert.Empty(h.Warn);
    }

    [Theory]
    [InlineData("RST", true)]
    [InlineData("RST.Core", true)]
    [InlineData("RST.UI", true)]
    [InlineData("rst.engine", true)]
    [InlineData("RST.UI.resources", true)]
    [InlineData("RSTX", false)]
    [InlineData("Serilog", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsRstAssembly_IsRstAndRstDotAnything(string? name, bool expected) =>
        Assert.Equal(expected, EngineDependencyResolver.IsRstAssembly(name));

    [Fact]
    public void NotAnEngineDependency_ReturnsNull_WithoutLoadingOrLogging()
    {
        var h = new Harness { PathFor = _ => null };

        var result = h.Build().Resolve(Name("RST.NotShipped", "1.0.0.0"));

        Assert.Null(result);
        Assert.Empty(h.LoadedPaths);
        Assert.Empty(h.Info);
        Assert.Empty(h.Warn);
    }

    [Fact]
    public void NothingLoaded_RequestedNewerThanShipped_Declines_AndWarnsWithBothVersionsAndPath()
    {
        // Nothing has loaded RST.Core yet and a requester wants 4.2 while RST ships 4.0: decline.
        var h = new Harness();

        var result = h.Build().Resolve(Name("RST.Core", "4.2.0.0"));

        Assert.Null(result);
        Assert.Empty(h.LoadedPaths);
        var warning = Assert.Single(h.Warn);
        Assert.Contains("RST.Core", warning);
        Assert.Contains("4.2.0.0", warning);
        Assert.Contains("4.0.0.0", warning);
        Assert.Contains(CorePath, warning);
        Assert.Contains("ZZZ-SerilogProbe", warning);
    }

    [Fact]
    public void LoadedOlderSameMajor_IsHandedOver_WithAWarningNamingRequestedGivenAndRequester()
    {
        // Decision #32: an older copy of the same major is loaded and a newer minor is asked for.
        // Refusing makes the requester fail to load at all; handing over the loaded copy lets it run.
        var h = new Harness { InContext = () => new[] { typeof(object).Assembly, Loaded } };
        var requested = SameMajorNewer;

        var result = h.Build().Resolve(Name(Loaded.GetName().Name!, requested.ToString()));

        Assert.Same(Loaded, result);
        Assert.Empty(h.LoadedPaths);
        var warning = Assert.Single(h.Warn);
        Assert.Equal(
            $"  resolve managed: {Loaded.GetName().Name} {requested} requested by ZZZ-SerilogProbe; handing over the loaded {LoadedVersion} at {Loaded.Location} (same major; one copy per context; warned once per session)",
            warning);
    }

    [Fact]
    public void SameMajorHandOver_WarnsOncePerAssemblyPerSession()
    {
        var other = typeof(RST.UI.Health.HealthScanner).Assembly; // RST.UI: a second loaded assembly
        var otherVersion = other.GetName().Version!;
        var h = new Harness { InContext = () => new[] { Loaded, other } };
        var resolver = h.Build();
        var name = Loaded.GetName().Name!;

        Assert.Same(Loaded, resolver.Resolve(Name(name, SameMajorNewer.ToString())));
        Assert.Same(Loaded, resolver.Resolve(Name(name, SameMajorNewer.ToString())));
        Assert.Same(Loaded, resolver.Resolve(Name(name.ToUpperInvariant(), new Version(LoadedVersion.Major, LoadedVersion.Minor + 5, 0, 0).ToString())));
        Assert.Single(h.Warn);

        Assert.Same(other, resolver.Resolve(Name(other.GetName().Name!, new Version(otherVersion.Major, otherVersion.Minor + 1, 0, 0).ToString())));
        Assert.Equal(2, h.Warn.Count);
        Assert.Contains(other.GetName().Name!, h.Warn[1]);

        // A new session (a new resolver) warns again.
        var again = new Harness { InContext = () => new[] { Loaded } };
        again.Build().Resolve(Name(name, SameMajorNewer.ToString()));
        Assert.Single(again.Warn);
    }

    [Fact]
    public void SameMajorHandOver_WithUnknownRequester_SaysSo()
    {
        var h = new Harness { InContext = () => new[] { Loaded }, Requester = () => null };

        h.Build().Resolve(Name(Loaded.GetName().Name!, SameMajorNewer.ToString()));

        Assert.Contains("requested by an unknown requester;", Assert.Single(h.Warn));
    }

    [Fact]
    public void SameMajorHandOver_WithThrowingRequesterLookup_StillHandsOver()
    {
        var h = new Harness { InContext = () => new[] { Loaded }, Requester = () => throw new InvalidOperationException("no stack") };

        Assert.Same(Loaded, h.Build().Resolve(Name(Loaded.GetName().Name!, SameMajorNewer.ToString())));
        Assert.Contains("an unknown requester", Assert.Single(h.Warn));
    }

    [Fact]
    public void LikelyRequester_IsTheFirstCallerOutsideTheFrameworkAndTheBootstrap()
    {
        Assert.Equal("RST.RuntimeTests", EngineDependencyResolver.LikelyRequester());

        // And the default wiring uses it: the requester here is this test assembly.
        var warn = new List<string>();
        var resolver = new EngineDependencyResolver(
            _ => CorePath, _ => new Version(4, 0, 0, 0), () => new[] { Loaded },
            _ => typeof(object).Assembly, _ => { }, warn.Add);
        resolver.Resolve(Name(Loaded.GetName().Name!, SameMajorNewer.ToString()));
        Assert.Contains("requested by RST.RuntimeTests;", Assert.Single(warn));
    }

    [Theory]
    [InlineData(0)] // the same version as loaded
    [InlineData(1)] // a lower version than loaded
    public void AlreadyLoadedAtOrAboveRequested_ReturnsThatCopy_WithoutLoadingByPath(int lower)
    {
        var requested = lower == 0 ? LoadedVersion : new Version(LoadedVersion.Major, 0, 0, 0);
        var h = new Harness
        {
            VersionAt = _ => new Version(LoadedVersion.Major + 10, 0, 0, 0),
            InContext = () => new[] { typeof(object).Assembly, Loaded },
        };

        var result = h.Build().Resolve(Name(Loaded.GetName().Name!, requested.ToString()));

        Assert.Same(Loaded, result);
        Assert.Empty(h.LoadedPaths);
        Assert.Empty(h.Warn);
        Assert.Contains(h.Info, m => m.Contains("already loaded", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadedOlderDifferentMajor_IsRefused_AndWarnsWithBothVersionsAndLoadedPath()
    {
        // A different major is not handed over, and loading ours by path would throw
        // FileLoadException inside the event.
        var requested = new Version(LoadedVersion.Major + 1, 0, 0, 0);
        var h = new Harness
        {
            VersionAt = _ => new Version(LoadedVersion.Major + 10, 0, 0, 0),
            InContext = () => new[] { Loaded },
        };
        var resolver = h.Build();

        var result = resolver.Resolve(Name(Loaded.GetName().Name!, requested.ToString()));

        Assert.Null(result);
        Assert.Empty(h.LoadedPaths);
        var warning = Assert.Single(h.Warn);
        Assert.Equal(
            $"  resolve managed: {Loaded.GetName().Name} {requested} requested by ZZZ-SerilogProbe, {LoadedVersion} already loaded at {Loaded.Location}; declining (different major; a second copy cannot load into this context)",
            warning);

        // Refusals are not limited to one per session.
        resolver.Resolve(Name(Loaded.GetName().Name!, requested.ToString()));
        Assert.Equal(2, h.Warn.Count);
    }

    [Fact]
    public void NameMatching_IsCaseInsensitive_AndIgnoresOtherNames()
    {
        var h = new Harness
        {
            VersionAt = _ => new Version(LoadedVersion.Major + 10, 0, 0, 0),
            InContext = () => new[] { typeof(object).Assembly, Loaded },
        };

        var result = h.Build().Resolve(Name(Loaded.GetName().Name!.ToUpperInvariant(), LoadedVersion.ToString()));

        Assert.Same(Loaded, result);
    }

    [Fact]
    public void NotLoaded_AndRequestedAtOrBelowShipped_LoadsOurCopyByPath()
    {
        var h = new Harness { InContext = () => new[] { typeof(object).Assembly } };

        var result = h.Build().Resolve(Name("RST.Core", "4.0.0.0"));

        Assert.Same(h.LoadResult, result);
        Assert.Equal(new[] { CorePath }, h.LoadedPaths);
        Assert.Empty(h.Warn);
        Assert.Contains(h.Info, m => m.Contains("RST.Core", StringComparison.Ordinal) && m.Contains(CorePath, StringComparison.Ordinal));
    }

    [Fact]
    public void RequestWithoutVersion_IsTreatedAsAnyVersion()
    {
        var h = new Harness();

        var result = h.Build().Resolve(Name("RST.Core", null));

        Assert.Same(h.LoadResult, result);
        Assert.Equal(new[] { CorePath }, h.LoadedPaths);
    }

    [Fact]
    public void ShippedVersionUnreadable_StillLoadsByPath()
    {
        var h = new Harness { VersionAt = _ => null };

        var result = h.Build().Resolve(Name("RST.Core", "4.0.0.0"));

        Assert.Same(h.LoadResult, result);
    }

    public static IEnumerable<object[]> FailingSeams() => new[]
    {
        new object[] { "pathFor" },
        new object[] { "versionAt" },
        new object[] { "inContext" },
        new object[] { "load" },
    };

    [Theory]
    [MemberData(nameof(FailingSeams))]
    public void AnyFailure_IsLoggedAsWarning_AndReturnsNull_NeverThrows(string seam)
    {
        var h = new Harness();
        switch (seam)
        {
            case "pathFor": h.PathFor = _ => throw new InvalidOperationException("deps.json unreadable"); break;
            case "versionAt": h.VersionAt = _ => throw new BadImageFormatException("not an assembly"); break;
            case "inContext": h.InContext = () => throw new InvalidOperationException("context unloading"); break;
            case "load": h.Load = _ => throw new FileLoadException("Assembly with same name is already loaded"); break;
        }

        var resolver = h.Build();
        var ex = Record.Exception(() => resolver.Resolve(Name("RST.Core", "4.0.0.0")));

        Assert.Null(ex);
        Assert.Null(resolver.Resolve(Name("RST.Core", "4.0.0.0")));
        Assert.Contains(h.Warn, m => m.Contains("failed, declining", StringComparison.Ordinal) && m.Contains("RST.Core", StringComparison.Ordinal));
    }

    [Fact]
    public void ThrowingLogger_DoesNotBreakResolution()
    {
        var resolver = new EngineDependencyResolver(
            _ => CorePath,
            _ => new Version(4, 0, 0, 0),
            Array.Empty<Assembly>,
            _ => typeof(object).Assembly,
            _ => throw new IOException("log file locked"),
            _ => throw new IOException("log file locked"));

        Assert.Same(typeof(object).Assembly, resolver.Resolve(Name("RST.Core", "4.0.0.0")));
        Assert.Null(resolver.Resolve(Name("RST.Core", "4.2.0.0")));
    }

    [Fact]
    public void FindLoaded_SkipsDynamicAssemblies()
    {
        var dynamic = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("RST.Core"), System.Reflection.Emit.AssemblyBuilderAccess.Run);

        Assert.Null(EngineDependencyResolver.FindLoaded(new Assembly[] { dynamic }, "RST.Core"));
        Assert.Null(EngineDependencyResolver.FindLoaded(new[] { Loaded }, null));
        Assert.Same(Loaded, EngineDependencyResolver.FindLoaded(new Assembly[] { dynamic, Loaded }, Loaded.GetName().Name));
    }

    // --- the startup "Third-party DLLs shipped" line --------------------------------------------

    [Fact]
    public void DescribeShipped_OnlyRstDlls_IsInfoSayingNone()
    {
        var (warn, message) = EngineDependencyResolver.DescribeShipped(
            new[] { "RST.Engine.dll", "RST.UI.dll", "RST.Core.dll", @"en\RST.UI.resources.dll" });

        Assert.False(warn);
        Assert.Equal("Third-party DLLs shipped: none (4 RST DLLs in the app folder; decision #33)", message);
    }

    [Fact]
    public void DescribeShipped_AThirdPartyDll_IsAWarningNamingEachOne()
    {
        var (warn, message) = EngineDependencyResolver.DescribeShipped(
            new[] { "RST.Engine.dll", "Serilog.dll", @"runtimes\win\lib\net8.0\System.Management.dll", "RST.Core.dll" });

        Assert.True(warn);
        Assert.Equal(
            @"Third-party DLLs shipped: 2 (runtimes\win\lib\net8.0\System.Management.dll, Serilog.dll); RST should ship none (decision #33)",
            message);
    }

    [Fact]
    public void DescribeShipped_EmptyFolder_IsInfo()
    {
        var (warn, message) = EngineDependencyResolver.DescribeShipped(Array.Empty<string>());

        Assert.False(warn);
        Assert.Contains("none", message);
    }

    // --- the production wiring -----------------------------------------------------------------

    [Fact]
    public void ForEngine_ResolvesByNameThroughARealCollectibleContext_AndHandsOverItsLoadedCopy()
    {
        // SC-013 N3: Default can satisfy even an unused reference from our deps.json before
        // Resolving fires. The fixture has its own deps.json in an isolated output subdirectory.
        const string fixture = "RST.ResolverFixture";
        Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies, a => a.GetName().Name == fixture);
        var context = new AssemblyLoadContext("RST resolver fresh", isCollectible: true);
        var borrower = new AssemblyLoadContext("RST resolver hand-over", isCollectible: true);
        try
        {
            var deps = new AssemblyDependencyResolver(Path.Combine(AppContext.BaseDirectory, "ResolverFixture", fixture + ".dll"));
            var name = new AssemblyName(fixture);
            var path = deps.ResolveAssemblyToPath(name);
            Assert.NotNull(path);
            var info = new List<string>();
            var warn = new List<string>();
            var resolver = EngineDependencyResolver.ForEngine(deps, context, info.Add, warn.Add);
            var calls = 0;
            Assembly? Resolve(AssemblyLoadContext _, AssemblyName requested)
            {
                calls++;
                return resolver.Resolve(requested);
            }
            context.Resolving += Resolve;
            var loaded = context.LoadFromAssemblyName(name);
            Assert.Equal(1, calls);
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(loaded));
            Assert.Equal(path, loaded.Location);
            Assert.Equal(AssemblyName.GetAssemblyName(path).Version, loaded.GetName().Version);

            // A second context requests the compatible copy owned by the resolver's context.
            // Asking context again would bypass Resolving via the runtime's own assembly cache.
            borrower.Resolving += Resolve;
            Assert.Same(loaded, borrower.LoadFromAssemblyName(loaded.GetName()));
            Assert.Equal(2, calls);
            Assert.Contains(info, m => m.Contains("already loaded", StringComparison.Ordinal));
            Assert.Empty(warn);
        }
        finally
        {
            borrower.Unload();
            context.Unload();
        }
    }

    [Fact]
    public void ForEngine_OverARealDepsJson_ReusesTheLoadedCopy_DeclinesANewerRequest_AndNeverAThirdPartyOne()
    {
        // This test assembly's own deps.json lists RST.Core (a project reference), which is already
        // loaded into the default context: the same shape as the engine's deps.json. It also lists
        // xunit.assert, a third-party package, which the resolver must not answer for.
        var info = new List<string>();
        var warn = new List<string>();
        var deps = new AssemblyDependencyResolver(typeof(EngineDependencyResolverTests).Assembly.Location);
        Assert.NotNull(deps.ResolveAssemblyToPath(typeof(Assert).Assembly.GetName()));
        var resolver = EngineDependencyResolver.ForEngine(deps, AssemblyLoadContext.Default, info.Add, warn.Add);
        var name = Loaded.GetName().Name!;

        Assert.Same(Loaded, resolver.Resolve(Name(name, LoadedVersion.ToString())));
        Assert.Empty(warn);

        var newer = new Version(LoadedVersion.Major + 1, 0, 0, 0);
        Assert.Null(resolver.Resolve(Name(name, newer.ToString())));
        var warning = Assert.Single(warn);
        Assert.Contains(newer.ToString(), warning);
        Assert.Contains(LoadedVersion.ToString(), warning);

        Assert.Null(resolver.Resolve(typeof(Assert).Assembly.GetName()));
        Assert.Null(resolver.Resolve(Name("RST.NotADependency", "1.0.0.0")));
        Assert.Single(warn);
    }
}
