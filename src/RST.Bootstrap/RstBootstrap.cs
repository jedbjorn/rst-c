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
    private static readonly string AppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RST", "app");

    private IExternalApplication? _engine;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            BootLog.Info("RST.Bootstrap loading");
            BootLog.Info($"  bootstrapDll={typeof(RstBootstrap).Assembly.Location}");
            BootLog.Info($"  appDir={AppDir} (exists={Directory.Exists(AppDir)})");

            if (!Directory.Exists(AppDir))
            {
                BootLog.Error($"App dir missing: {AppDir}. Engine cannot load.");
                return Result.Failed;
            }

            var enginePath = Path.Combine(AppDir, "RST.Engine.dll");
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
            // WebView2 (incl. WebView2Loader.dll), System.Management, etc.
            var resolver = new AssemblyDependencyResolver(enginePath);
            AssemblyLoadContext.Default.Resolving += (ctx, name) =>
            {
                var path = resolver.ResolveAssemblyToPath(name);
                if (path is null) return null;
                BootLog.Info($"  resolve managed: {name.Name} -> {path}");
                return ctx.LoadFromAssemblyPath(path);
            };
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += (asm, name) =>
            {
                var path = resolver.ResolveUnmanagedDllToPath(name);
                if (path is null) return IntPtr.Zero;
                BootLog.Info($"  resolve native: {name} -> {path}");
                return NativeLibrary.Load(path);
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
            var result = _engine.OnStartup(application);
            BootLog.Info($"OnStartup returned {result}");
            return result;
        }
        catch (Exception ex)
        {
            BootLog.Error("OnStartup threw", ex);
            return Result.Failed;
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
