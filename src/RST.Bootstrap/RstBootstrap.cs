// RstBootstrap.cs — IExternalApplication thunk that loads the engine from
// %AppData%\RST\app\ and forwards lifecycle calls. See RST-033.

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
            // are not interchangeable between majors. Layout:
            //   %AppData%\RST\R25\app\RST.Engine.dll
            //   %AppData%\RST\R26\app\RST.Engine.dll
            //   %AppData%\RST\R27\app\RST.Engine.dll
            var revitVersion = application.ControlledApplication.VersionNumber;
            var major = "R" + (revitVersion.Length >= 2 ? revitVersion.Substring(revitVersion.Length - 2) : revitVersion);
            var appDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RST", major, "app");

            BootLog.Info("RST.Bootstrap loading");
            BootLog.Info($"  bootstrapDll={typeof(RstBootstrap).Assembly.Location}");
            BootLog.Info($"  revitVersion={revitVersion} major={major}");
            BootLog.Info($"  appDir={appDir} (exists={Directory.Exists(appDir)})");

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
            // the engine DLL and resolves both managed deps and native libs
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
/// The bootstrap's managed <c>Resolving</c> handler (decision #27, research M3). It answers only
/// for assemblies in the engine's deps.json (Serilog, Serilog.Sinks.File, System.Management,
/// RST.Core, RST.UI). For those it:
/// <list type="number">
/// <item>declines (returns null) when the requested version is newer than the copy RST ships, so
/// a neighbour add-in is never handed an older copy than it asked for;</item>
/// <item>reuses a same-named assembly already loaded in the context when its version is at or
/// above the requested one, instead of loading ours by path;</item>
/// <item>declines with a warning naming both versions and the loaded path when an older copy is
/// already loaded (a second copy cannot load into the same context);</item>
/// <item>never throws: any failure is logged as a warning and the handler returns null.</item>
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

    public EngineDependencyResolver(
        Func<AssemblyName, string?> pathFor,
        Func<string, Version?> versionAt,
        Func<IEnumerable<Assembly>> loaded,
        Func<string, Assembly> loadFromPath,
        Action<string> info,
        Action<string> warn)
    {
        _pathFor = pathFor;
        _versionAt = versionAt;
        _loaded = loaded;
        _loadFromPath = loadFromPath;
        _info = info;
        _warn = warn;
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
        var shipped = _versionAt(path);
        if (shipped is not null && shipped < wanted)
        {
            Safe(_warn, $"  resolve managed: {requested.Name} {wanted} requested, RST ships {shipped} at {path}; declining (never hand out an older copy)");
            return null;
        }

        var loaded = FindLoaded(_loaded(), requested.Name);
        if (loaded is not null)
        {
            var have = loaded.GetName().Version ?? Any;
            if (have >= wanted)
            {
                Safe(_info, $"  resolve managed: {requested.Name} {wanted} -> already loaded {have} at {LocationOf(loaded)}");
                return loaded;
            }
            Safe(_warn, $"  resolve managed: {requested.Name} {wanted} requested, {have} already loaded at {LocationOf(loaded)}; declining (a second copy cannot load into this context)");
            return null;
        }

        Safe(_info, $"  resolve managed: {requested.Name} -> {path}");
        return _loadFromPath(path);
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
