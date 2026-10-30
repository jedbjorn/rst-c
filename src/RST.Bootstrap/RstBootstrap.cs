// RstBootstrap.cs — IExternalApplication thunk that loads the engine and
// forwards lifecycle calls. See RST-033. The engine is in <bootstrap dir>\app\
// when that folder exists (RST-Machine.msi, feature #17 spec #20 §7), otherwise
// in %AppData%\RST\R<NN>\app\ (RST.msi, per user): see EngineLocation below.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Autodesk.Revit.UI;

namespace RST.Bootstrap;

[Autodesk.Revit.Attributes.Regeneration(Autodesk.Revit.Attributes.RegenerationOption.Manual)]
[Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
public sealed class RstBootstrap : IExternalApplication
{
    private IExternalApplication? _engine;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            // Derive the per-major app dir from Revit's reported version
            // (RST-037). Each Revit major pins different Nice3point.Revit.Api
            // versions and 2027 targets a different TFM, so engine binaries
            // are not interchangeable between majors. The two layouts
            // (EngineLocation):
            //   per machine: C:\Program Files\RST\R<NN>\RST.Bootstrap.dll
            //                C:\Program Files\RST\R<NN>\app\RST.Engine.dll
            //   per user:    %AppData%\Autodesk\Revit\Addins\<year>\RST.Bootstrap.dll
            //                %AppData%\RST\R<NN>\app\RST.Engine.dll
            var revitVersion = application.ControlledApplication.VersionNumber;
            var major = "R" + (revitVersion.Length >= 2 ? revitVersion.Substring(revitVersion.Length - 2) : revitVersion);
            var bootstrapDll = typeof(RstBootstrap).Assembly.Location;
            var (appDir, layout) = EngineLocation.Resolve(
                string.IsNullOrEmpty(bootstrapDll) ? null : Path.GetDirectoryName(bootstrapDll),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                major,
                Directory.Exists);

            BootLog.Info("RST.Bootstrap loading");
            BootLog.Info($"  bootstrapDll={bootstrapDll}");
            BootLog.Info($"  revitVersion={revitVersion} major={major}");
            BootLog.Info($"  appDir={appDir} (exists={Directory.Exists(appDir)}, layout: {layout})");

            if (!Directory.Exists(appDir))
            {
                BootLog.Error($"App dir missing: {appDir}. Engine cannot load.");
                return Result.Failed;
            }

            var enginePath = Path.Combine(appDir, "RST.Engine.dll");
            var fi = new FileInfo(enginePath);
            BootLog.Info($"  enginePath={enginePath} (exists={fi.Exists}, size={(fi.Exists ? fi.Length : 0)} bytes)");
            if (!fi.Exists)
            {
                BootLog.Error("RST.Engine.dll not found at expected path.");
                return Result.Failed;
            }

            // AssemblyDependencyResolver reads RST.Engine.deps.json next to
            // the engine DLL (so it probes the engine dir chosen above, in
            // either layout) and resolves both managed deps and native libs
            // (runtimes/<rid>/native/*) for every transitive — Serilog,
            // System.Management (its runtime variant), etc.
            //
            // The handler is process-wide: it answers any requester in the
            // default context, not only the engine. EngineDependencyResolver
            // (below) keeps it from handing a neighbour an older copy, and
            // neither handler throws from inside the event (decision #27).
            var resolver = new AssemblyDependencyResolver(enginePath);
            var managed = EngineDependencyResolver.ForEngine(resolver, AssemblyLoadContext.Default, BootLog.Info, BootLog.Warn);
            AssemblyLoadContext.Default.Resolving += (ctx, name) => managed.Resolve(name);
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += (asm, name) =>
            {
                try
                {
                    var path = resolver.ResolveUnmanagedDllToPath(name);
                    if (path is null) return IntPtr.Zero;
                    BootLog.Info($"  resolve native: {name} -> {path}");
                    return NativeLibrary.Load(path);
                }
                catch (Exception ex)
                {
                    BootLog.Warn($"  resolve native: {name} failed, declining :: {ex.GetType().Name}: {ex.Message}");
                    return IntPtr.Zero;
                }
            };
            BootLog.Info("AssemblyDependencyResolver registered");

            var engineAsm = AssemblyLoadContext.Default.LoadFromAssemblyPath(enginePath);
            BootLog.Info($"Engine loaded: {engineAsm.FullName}");

            const string typeName = "RST.Engine.RstApplication";
            var engineType = engineAsm.GetType(typeName, throwOnError: false);
            if (engineType is null)
            {
                BootLog.Error($"Type {typeName} not found in engine assembly.");
                return Result.Failed;
            }

            _engine = Activator.CreateInstance(engineType) as IExternalApplication;
            if (_engine is null)
            {
                BootLog.Error($"{typeName} does not implement IExternalApplication.");
                return Result.Failed;
            }

            BootLog.Info("Forwarding OnStartup -> engine");
            Result result;
            try
            {
                result = _engine.OnStartup(application);
            }
            finally
            {
                // Once per startup, after the engine has configured Serilog
                // (or failed to): the Serilog the engine's context holds.
                LogBoundSerilog(engineAsm);
            }
            BootLog.Info($"OnStartup returned {result}");
            return result;
        }
        catch (Exception ex)
        {
            BootLog.Error("OnStartup threw", ex);
            return Result.Failed;
        }
    }

    private static void LogBoundSerilog(Assembly engineAsm)
    {
        try
        {
            const string name = "Serilog";
            var compiled = Array.Find(engineAsm.GetReferencedAssemblies(),
                r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))?.Version;
            var context = AssemblyLoadContext.GetLoadContext(engineAsm) ?? AssemblyLoadContext.Default;
            var bound = EngineDependencyResolver.FindLoaded(context.Assemblies, name);
            var (warn, message) = EngineDependencyResolver.DescribeBinding(
                name, compiled, bound?.GetName().Version, bound is null ? null : EngineDependencyResolver.LocationOf(bound));
            if (warn) BootLog.Warn(message); else BootLog.Info(message);
        }
        catch (Exception ex)
        {
            BootLog.Warn($"Serilog binding check failed :: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        if (_engine is null)
        {
            BootLog.Info("OnShutdown called with no engine loaded; nothing to forward.");
            return Result.Succeeded;
        }
        try
        {
            BootLog.Info("Forwarding OnShutdown -> engine");
            var result = _engine.OnShutdown(application);
            BootLog.Info($"OnShutdown returned {result}");
            return result;
        }
        catch (Exception ex)
        {
            BootLog.Error("OnShutdown threw", ex);
            return Result.Failed;
        }
    }
}

/// <summary>
/// Where the engine is (feature #17, spec #20 §7). RST-Machine.msi puts the bootstrap in
/// <c>C:\Program Files\RST\R&lt;NN&gt;\</c> and the engine in its <c>app\</c> subfolder. RST.msi puts the
/// bootstrap beside its manifest in the per-user Add-Ins folder and the engine in
/// <c>%AppData%\RST\R&lt;NN&gt;\app\</c>. So: <c>&lt;bootstrap dir&gt;\app\</c> when that folder exists,
/// otherwise the per-user folder, which is every per-user install before and after v3.1. Pure, with
/// the folder check as a seam, so RST.RuntimeTests covers both layouts without Revit.
/// </summary>
internal static class EngineLocation
{
    public const string BesideBootstrap = "beside the bootstrap (per machine)";
    public const string PerUser = "per user (%AppData%)";

    public static (string AppDir, string Layout) Resolve(
        string? bootstrapDir, string appData, string major, Func<string, bool> directoryExists)
    {
        if (!string.IsNullOrEmpty(bootstrapDir))
        {
            var beside = Path.Combine(bootstrapDir, "app");
            if (directoryExists(beside)) return (beside, BesideBootstrap);
        }
        return (Path.Combine(appData, "RST", major, "app"), PerUser);
    }
}

/// <summary>
/// The bootstrap's managed <c>Resolving</c> handler (decisions #27 and #32, research M3). It
/// answers only for assemblies in the engine's deps.json (Serilog, Serilog.Sinks.File,
/// System.Management, RST.Core, RST.UI). For those:
/// <list type="number">
/// <item>a same-named assembly already loaded in the context at or above the requested version
/// is reused, instead of loading ours by path;</item>
/// <item>an already-loaded copy older than requested but of the <b>same major</b> version is
/// handed over (decision #32): only one copy can load into the shared context, and a requester
/// failing to load is worse than it running on the older minor. One <c>WRN</c> per assembly
/// per session names the requested version, the version given and the likely requester;</item>
/// <item>an already-loaded copy of a <b>different major</b> version is refused (null) with a
/// <c>WRN</c> naming both versions and the loaded path;</item>
/// <item>when nothing is loaded yet, a request above the copy RST ships is declined (null), so
/// the requester's own newer copy can load and RST later binds up to it;</item>
/// <item>it never throws: any failure is logged as a warning and the handler returns null.</item>
/// </list>
/// Its seams are delegates so RST.RuntimeTests can drive every branch without Revit. It cannot
/// stop a neighbour's own resolver handing RST an older copy (research M3); the startup
/// <see cref="DescribeBinding"/> line makes that case visible in the boot log.
/// </summary>
internal sealed class EngineDependencyResolver
{
    private static readonly Version Any = new(0, 0, 0, 0);

    private readonly Func<AssemblyName, string?> _pathFor;
    private readonly Func<string, Version?> _versionAt;
    private readonly Func<IEnumerable<Assembly>> _loaded;
    private readonly Func<string, Assembly> _loadFromPath;
    private readonly Action<string> _info;
    private readonly Action<string> _warn;
    private readonly Func<string?> _requester;
    private readonly HashSet<string> _handOverWarned = new(StringComparer.OrdinalIgnoreCase);

    public EngineDependencyResolver(
        Func<AssemblyName, string?> pathFor,
        Func<string, Version?> versionAt,
        Func<IEnumerable<Assembly>> loaded,
        Func<string, Assembly> loadFromPath,
        Action<string> info,
        Action<string> warn,
        Func<string?>? requester = null)
    {
        _pathFor = pathFor;
        _versionAt = versionAt;
        _loaded = loaded;
        _loadFromPath = loadFromPath;
        _info = info;
        _warn = warn;
        _requester = requester ?? LikelyRequester;
    }

    /// <summary>The production wiring: the engine's deps.json resolver over one load context.</summary>
    public static EngineDependencyResolver ForEngine(
        AssemblyDependencyResolver resolver, AssemblyLoadContext context, Action<string> info, Action<string> warn) =>
        new(resolver.ResolveAssemblyToPath,
            path => AssemblyName.GetAssemblyName(path).Version,
            () => context.Assemblies,
            context.LoadFromAssemblyPath,
            info,
            warn);

    public Assembly? Resolve(AssemblyName requested)
    {
        try
        {
            return ResolveCore(requested);
        }
        catch (Exception ex)
        {
            Safe(_warn, $"  resolve managed: {requested.Name} {requested.Version} failed, declining :: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private Assembly? ResolveCore(AssemblyName requested)
    {
        var path = _pathFor(requested);
        if (path is null) return null; // not one of the engine's dependencies

        var wanted = requested.Version ?? Any;
        var loaded = FindLoaded(_loaded(), requested.Name);
        if (loaded is not null)
        {
            var have = loaded.GetName().Version ?? Any;
            if (have >= wanted)
            {
                Safe(_info, $"  resolve managed: {requested.Name} {wanted} -> already loaded {have} at {LocationOf(loaded)}");
                return loaded;
            }
            if (have.Major == wanted.Major)
            {
                // Decision #32: same major, so hand over the loaded copy rather than let the
                // requester fail; a second copy cannot load into this context.
                bool first;
                lock (_handOverWarned) first = _handOverWarned.Add(requested.Name ?? "");
                if (first)
                    Safe(_warn, $"  resolve managed: {requested.Name} {wanted} requested by {RequesterText()}; handing over the loaded {have} at {LocationOf(loaded)} (same major; one copy per context; warned once per session)");
                return loaded;
            }
            Safe(_warn, $"  resolve managed: {requested.Name} {wanted} requested by {RequesterText()}, {have} already loaded at {LocationOf(loaded)}; declining (different major; a second copy cannot load into this context)");
            return null;
        }

        var shipped = _versionAt(path);
        if (shipped is not null && shipped < wanted)
        {
            // Nothing loaded yet: let the requester's own newer copy load instead of ours.
            Safe(_warn, $"  resolve managed: {requested.Name} {wanted} requested by {RequesterText()}, RST ships {shipped} at {path} and none is loaded; declining (never hand out an older copy)");
            return null;
        }

        Safe(_info, $"  resolve managed: {requested.Name} -> {path}");
        return _loadFromPath(path);
    }

    private string RequesterText()
    {
        try
        {
            return _requester() ?? "an unknown requester";
        }
        catch
        {
            return "an unknown requester";
        }
    }

    /// <summary>
    /// Best effort: <c>AssemblyLoadContext.Resolving</c> does not name the requester, so take the
    /// first managed frame below the load context's resolving-event frames that is outside the
    /// runtime, the framework and this assembly: the code whose JIT or type load asked for the
    /// assembly. Without such frames (a direct call), the first such frame of the whole stack.
    /// Null when there is none.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static string? LikelyRequester()
    {
        var self = typeof(EngineDependencyResolver).Assembly;
        var frames = new System.Diagnostics.StackTrace(1, false).GetFrames();
        var start = 0;
        for (var i = 0; i < frames.Length; i++)
            if (frames[i].GetMethod()?.DeclaringType == typeof(AssemblyLoadContext)) start = i + 1;
        foreach (var frame in frames.Skip(start))
        {
            var asm = frame.GetMethod()?.DeclaringType?.Assembly;
            if (asm is null || asm == self || asm.IsDynamic) continue;
            var name = asm.GetName().Name ?? "";
            if (name.StartsWith("System", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                || name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase)
                || name.Equals("netstandard", StringComparison.OrdinalIgnoreCase))
                continue;
            return name;
        }
        return null;
    }

    /// <summary>The first non-dynamic assembly with this simple name (case-insensitive), or null.</summary>
    public static Assembly? FindLoaded(IEnumerable<Assembly> assemblies, string? simpleName)
    {
        if (string.IsNullOrEmpty(simpleName)) return null;
        foreach (var a in assemblies)
        {
            if (a.IsDynamic) continue;
            if (string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase)) return a;
        }
        return null;
    }

    public static string LocationOf(Assembly assembly)
    {
        try
        {
            var location = assembly.Location;
            return string.IsNullOrEmpty(location) ? "(no path)" : location;
        }
        catch
        {
            return "(no path)";
        }
    }

    /// <summary>
    /// The startup line for the version a dependency actually bound to: a warning when it is not
    /// loaded, or is older than the version the engine was compiled against.
    /// </summary>
    public static (bool Warn, string Message) DescribeBinding(string name, Version? compiled, Version? bound, string? location)
    {
        var against = compiled?.ToString() ?? "unknown";
        if (bound is null)
            return (true, $"{name} bound: not loaded (engine compiled against {against})");
        var message = $"{name} bound: {bound} at {location ?? "(no path)"} (engine compiled against {against})";
        return compiled is not null && bound < compiled
            ? (true, message + "; OLDER than compiled, another add-in supplied it")
            : (false, message);
    }

    private static void Safe(Action<string> log, string message)
    {
        try { log(message); } catch { /* logging must never break resolution */ }
    }
}
