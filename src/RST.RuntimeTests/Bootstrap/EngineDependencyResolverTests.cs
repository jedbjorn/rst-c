// rst-c's own (not vendored): the bootstrap's assembly resolver (decisions #27 and #32, research M3).

using System.IO; // not in WPF's implicit usings
using System.Reflection;
using System.Runtime.Loader;
using RST.Bootstrap;
using Xunit;

namespace RST.RuntimeTests.Bootstrap;

/// <summary>
/// Every branch of <see cref="EngineDependencyResolver"/>, the bootstrap's process-wide
/// <c>AssemblyLoadContext.Default.Resolving</c> handler: not ours, nothing loaded and requested
/// newer than shipped, a compatible copy already loaded, an older same-major copy already loaded
/// (handed over with one warning per session, decision #32), an older different-major copy (refused),
/// a fresh load by path, a request without a version, and every failure (it must never throw from
/// inside the event). Plus the startup "Serilog bound" line, the requester lookup, and the
/// production wiring over a real deps.json resolver.
/// </summary>
public class EngineDependencyResolverTests
{
    private const string SerilogPath = @"C:\Users\u\AppData\Roaming\RST\R26\app\Serilog.dll";

    /// <summary>A loaded, non-dynamic assembly whose version and path the tests can read.</summary>
    private static readonly Assembly Loaded = typeof(Assert).Assembly; // xunit.assert

    private static Version LoadedVersion => Loaded.GetName().Version!;

    private sealed class Harness
    {
        public List<string> Info { get; } = new();
        public List<string> Warn { get; } = new();
        public List<string> LoadedPaths { get; } = new();

        public Func<AssemblyName, string?> PathFor { get; set; } = _ => SerilogPath;
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

    /// <summary>A version above the loaded one with the same major (a 4.0 loaded, 4.2 asked).</summary>
    private static Version SameMajorNewer => new(LoadedVersion.Major, LoadedVersion.Minor + 2, 0, 0);

    private static AssemblyName Name(string name, string? version) =>
        new(version is null ? name : $"{name}, Version={version}");

    [Fact]
    public void NotAnEngineDependency_ReturnsNull_WithoutLoadingOrLogging()
    {
        var h = new Harness { PathFor = _ => null };

        var result = h.Build().Resolve(Name("Newtonsoft.Json", "13.0.0.0"));

        Assert.Null(result);
        Assert.Empty(h.LoadedPaths);
        Assert.Empty(h.Info);
        Assert.Empty(h.Warn);
    }

    [Fact]
    public void NothingLoaded_RequestedNewerThanShipped_Declines_AndWarnsWithBothVersionsAndPath()
    {
        // Nothing has loaded Serilog yet and a requester wants 4.2 while RST ships 4.0: decline, so
        // the requester's own 4.2 can load (RST then binds up to it).
        var h = new Harness();

        var result = h.Build().Resolve(Name("Serilog", "4.2.0.0"));

        Assert.Null(result);
        Assert.Empty(h.LoadedPaths);
        var warning = Assert.Single(h.Warn);
        Assert.Contains("Serilog", warning);
        Assert.Contains("4.2.0.0", warning);
        Assert.Contains("4.0.0.0", warning);
        Assert.Contains(SerilogPath, warning);
        Assert.Contains("ZZZ-SerilogProbe", warning);
    }

    [Fact]
    public void LoadedOlderSameMajor_IsHandedOver_WithAWarningNamingRequestedGivenAndRequester()
    {
        // Decision #32: RST's Serilog 4.0 is loaded and ZZZ asks for 4.2. Refusing makes ZZZ fail
        // to load at all; handing over 4.0 lets it run.
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
        var other = typeof(FactAttribute).Assembly; // xunit.core: a second loaded assembly
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
            _ => SerilogPath, _ => new Version(4, 0, 0, 0), () => new[] { Loaded },
            _ => typeof(object).Assembly, _ => { }, warn.Add);
        resolver.Resolve(Name(Loaded.GetName().Name!, SameMajorNewer.ToString()));
        Assert.Contains("requested by RST.RuntimeTests;", Assert.Single(warn));
    }

    [Theory]
    [InlineData(0)] // the same version as loaded
    [InlineData(1)] // a lower version than loaded
    public void AlreadyLoadedAtOrAboveRequested_ReturnsThatCopy_WithoutLoadingByPath(int lower)
    {
        var requested = lower == 0 ? LoadedVersion : new Version(Math.Max(0, LoadedVersion.Major - 1), 0, 0, 0);
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
        // A neighbour loaded Serilog 3.1.1 (assembly 2.0.0.0) first; RST's engine asks for 4.0.
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
        var h = new Harness { InContext = () => new[] { Loaded } };

        var result = h.Build().Resolve(Name("Serilog", "4.0.0.0"));

        Assert.Same(h.LoadResult, result);
        Assert.Equal(new[] { SerilogPath }, h.LoadedPaths);
        Assert.Empty(h.Warn);
        Assert.Contains(h.Info, m => m.Contains("Serilog", StringComparison.Ordinal) && m.Contains(SerilogPath, StringComparison.Ordinal));
    }

    [Fact]
    public void RequestWithoutVersion_IsTreatedAsAnyVersion()
    {
        var h = new Harness();

        var result = h.Build().Resolve(Name("Serilog", null));

        Assert.Same(h.LoadResult, result);
        Assert.Equal(new[] { SerilogPath }, h.LoadedPaths);
    }

    [Fact]
    public void ShippedVersionUnreadable_StillLoadsByPath()
    {
        var h = new Harness { VersionAt = _ => null };

        var result = h.Build().Resolve(Name("Serilog", "4.0.0.0"));

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
        var ex = Record.Exception(() => resolver.Resolve(Name("Serilog", "4.0.0.0")));

        Assert.Null(ex);
        Assert.Null(resolver.Resolve(Name("Serilog", "4.0.0.0")));
        Assert.Contains(h.Warn, m => m.Contains("failed, declining", StringComparison.Ordinal) && m.Contains("Serilog", StringComparison.Ordinal));
    }

    [Fact]
    public void ThrowingLogger_DoesNotBreakResolution()
    {
        var resolver = new EngineDependencyResolver(
            _ => SerilogPath,
            _ => new Version(4, 0, 0, 0),
            Array.Empty<Assembly>,
            _ => typeof(object).Assembly,
            _ => throw new IOException("log file locked"),
            _ => throw new IOException("log file locked"));

        Assert.Same(typeof(object).Assembly, resolver.Resolve(Name("Serilog", "4.0.0.0")));
        Assert.Null(resolver.Resolve(Name("Serilog", "4.2.0.0")));
    }

    [Fact]
    public void FindLoaded_SkipsDynamicAssemblies()
    {
        var dynamic = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("Serilog"), System.Reflection.Emit.AssemblyBuilderAccess.Run);

        Assert.Null(EngineDependencyResolver.FindLoaded(new Assembly[] { dynamic }, "Serilog"));
        Assert.Null(EngineDependencyResolver.FindLoaded(new[] { Loaded }, null));
        Assert.Same(Loaded, EngineDependencyResolver.FindLoaded(new Assembly[] { dynamic, Loaded }, Loaded.GetName().Name));
    }

    // --- the startup "Serilog bound" line ------------------------------------------------------

    [Fact]
    public void DescribeBinding_SameAsCompiled_IsInfo()
    {
        var (warn, message) = EngineDependencyResolver.DescribeBinding("Serilog", new Version(4, 0, 0, 0), new Version(4, 0, 0, 0), SerilogPath);

        Assert.False(warn);
        Assert.Equal($"Serilog bound: 4.0.0.0 at {SerilogPath} (engine compiled against 4.0.0.0)", message);
    }

    [Fact]
    public void DescribeBinding_NewerThanCompiled_IsInfo()
    {
        var (warn, message) = EngineDependencyResolver.DescribeBinding("Serilog", new Version(4, 0, 0, 0), new Version(4, 2, 0, 0), @"C:\x\Serilog.dll");

        Assert.False(warn);
        Assert.Contains("4.2.0.0", message);
    }

    [Fact]
    public void DescribeBinding_OlderThanCompiled_IsWarning()
    {
        var (warn, message) = EngineDependencyResolver.DescribeBinding("Serilog", new Version(4, 0, 0, 0), new Version(2, 0, 0, 0), @"C:\neighbour\Serilog.dll");

        Assert.True(warn);
        Assert.Contains("2.0.0.0", message);
        Assert.Contains("4.0.0.0", message);
        Assert.Contains(@"C:\neighbour\Serilog.dll", message);
        Assert.Contains("OLDER", message);
    }

    [Fact]
    public void DescribeBinding_NotLoaded_IsWarning()
    {
        var (warn, message) = EngineDependencyResolver.DescribeBinding("Serilog", new Version(4, 0, 0, 0), null, null);

        Assert.True(warn);
        Assert.Contains("not loaded", message);
    }

    // --- the production wiring -----------------------------------------------------------------

    [Fact]
    public void ForEngine_OverARealDepsJson_ReusesTheLoadedCopy_AndDeclinesANewerRequest()
    {
        // This test assembly's own deps.json lists xunit.assert, which is already loaded into the
        // default context: the same shape as the engine's deps.json listing Serilog.
        var info = new List<string>();
        var warn = new List<string>();
        var deps = new AssemblyDependencyResolver(typeof(EngineDependencyResolverTests).Assembly.Location);
        var resolver = EngineDependencyResolver.ForEngine(deps, AssemblyLoadContext.Default, info.Add, warn.Add);
        var name = Loaded.GetName().Name!;

        Assert.Same(Loaded, resolver.Resolve(Name(name, LoadedVersion.ToString())));
        Assert.Empty(warn);

        var newer = new Version(LoadedVersion.Major + 1, 0, 0, 0);
        Assert.Null(resolver.Resolve(Name(name, newer.ToString())));
        var warning = Assert.Single(warn);
        Assert.Contains(newer.ToString(), warning);
        Assert.Contains(LoadedVersion.ToString(), warning);

        Assert.Null(resolver.Resolve(Name("RST.NotADependency", "1.0.0.0")));
    }
}
