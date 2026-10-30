// rst-c's own (not vendored): the Serilog probe add-ins for VM step V3 of the v3.0.0 resolver
// hardening (decision #27; shared/research/assembly-version-conflict.md, V3). Never shipped.
//
// One source, two add-ins (the csproj defines PROBE_AAA or PROBE_ZZZ):
//   AAA-SerilogProbe  Serilog 3.1.1 (assembly 2.0.0.0) + its own Resolving handler, the way many
//                     add-in templates ship one. Its manifest sorts before RST.addin.
//   ZZZ-SerilogProbe  Serilog 4.2.0 (assembly 4.2.0.0), no resolver of its own. Sorts after RST.addin.
//
// In OnStartup each one binds Serilog, writes a line through a private Serilog file logger, and
// writes %TEMP%\probe-<name>.txt. Line 1 is typeof(Serilog.Log).Assembly.FullName, or
// "FAILED <exception>" when Serilog could not be bound. The lines after it are detail: the path
// and load context Serilog came from, and every Serilog* assembly in the default context.
// OnStartup never fails Revit's load: every outcome is written down instead.

using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Autodesk.Revit.UI;
using Serilog; // WriteTo.File (Serilog.Sinks.File's extension lives in the Serilog namespace)

namespace SerilogProbe;

public sealed class ProbeApp : IExternalApplication
{
#if PROBE_AAA
    public const string ProbeName = "AAA-SerilogProbe";
#else
    public const string ProbeName = "ZZZ-SerilogProbe";
#endif

    public Result OnStartup(UIControlledApplication application)
    {
        var detail = new List<string>
        {
            "probe=" + ProbeName,
            "started=" + DateTime.Now.ToString("O"),
            "probeAssembly=" + typeof(ProbeApp).Assembly.Location,
            "revit=" + application.ControlledApplication.VersionBuild,
        };
        string first;
        try
        {
#if PROBE_AAA
            OwnResolver.Register(detail);
#endif
            first = BindSerilog(detail);
        }
        catch (Exception ex)
        {
            first = "FAILED " + ex.GetType().FullName + ": " + ex.Message;
            detail.Add("exception=" + ex);
        }

        detail.Add("defaultContextSerilog=" + string.Join(" | ", AssemblyLoadContext.Default.Assemblies
            .Where(a => !a.IsDynamic && (a.GetName().Name ?? "").StartsWith("Serilog", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.FullName + " @ " + a.Location)));

        try
        {
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "probe-" + ProbeName + ".txt"), new[] { first }.Concat(detail));
        }
        catch
        {
            // A probe must never stop Revit's start-up.
        }
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

    // Separate and not inlined: the JIT binds Serilog when it compiles this method, so a bind
    // failure throws here, inside OnStartup's try, rather than before OnStartup runs.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string BindSerilog(List<string> detail)
    {
        var serilog = typeof(Serilog.Log).Assembly;
        detail.Add("serilogPath=" + serilog.Location);
        detail.Add("serilogContext=" + (AssemblyLoadContext.GetLoadContext(serilog)?.Name ?? "?"));

        var logPath = Path.Combine(Path.GetTempPath(), "probe-" + ProbeName + ".log");
        using (var logger = new Serilog.LoggerConfiguration().WriteTo.File(logPath).CreateLogger())
        {
            logger.Information("{Probe} bound {Serilog}", ProbeName, serilog.FullName);
        }
        detail.Add("serilogLog=" + logPath);
        return serilog.FullName ?? "(no name)";
    }
}

#if PROBE_AAA
/// <summary>
/// AAA's own resolver: Serilog* from the probe's folder, by path, whatever version was asked for.
/// This is the "neighbour with a resolver" case of the research repro. Every request it answers
/// is appended to %TEMP%\probe-AAA-SerilogProbe-resolver.txt, so the run shows whether RST's
/// engine was handed AAA's copy.
/// </summary>
internal static class OwnResolver
{
    private static bool _registered;

    public static void Register(List<string> detail)
    {
        if (_registered) return;
        _registered = true;
        var dir = Path.GetDirectoryName(typeof(OwnResolver).Assembly.Location)!;
        var record = Path.Combine(Path.GetTempPath(), "probe-" + ProbeApp.ProbeName + "-resolver.txt");
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            if (!(name.Name ?? "").StartsWith("Serilog", StringComparison.OrdinalIgnoreCase)) return null;
            var path = Path.Combine(dir, name.Name + ".dll");
            if (!File.Exists(path)) return null;
            string outcome;
            System.Reflection.Assembly? result = null;
            try
            {
                result = ctx.LoadFromAssemblyPath(path);
                outcome = "handed " + result.GetName().Version;
            }
            catch (Exception ex)
            {
                outcome = "FAILED " + ex.GetType().Name + ": " + ex.Message;
            }
            try
            {
                File.AppendAllText(record, DateTime.Now.ToString("HH:mm:ss.fff") + " requested " + name.FullName + " -> " + outcome + Environment.NewLine);
            }
            catch
            {
                // best-effort record
            }
            return result;
        };
        detail.Add("ownResolver=registered for Serilog* in " + dir);
    }
}
#endif
