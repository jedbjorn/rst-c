// rst-c's own (not vendored): the retention-gate hook (spec #24, Cleanup,
// rule 4). A raw session file that is folded and sealed in both copies is
// deleted only when every registered gate allows it. Upstream registers
// none; a fork's shipper registers one (for example "only once shipped").
// A gate that throws refuses: a gate error must never delete data.

namespace RST.Core.Telemetry.Rollups;

/// <summary>A raw session file cleanup is about to delete.</summary>
public sealed record RetentionCandidate(
    string Path,
    string SessionGuid,
    IReadOnlyList<string> ModelIds,
    long SizeBytes,
    DateTimeOffset LastWriteUtc);

/// <summary>A fork's veto over deleting a folded session file.</summary>
public interface IRetentionGate
{
    /// <summary>True to allow deleting <paramref name="candidate"/>; false keeps it for now
    /// (it is asked again on the next pass).</summary>
    bool AllowsDelete(RetentionCandidate candidate);
}

/// <summary>The process-wide gate registry the tray worker consults.</summary>
public static class RetentionGates
{
    private static readonly object Gate = new();
    private static IRetentionGate[] _registered = Array.Empty<IRetentionGate>();

    public static IReadOnlyList<IRetentionGate> Registered
    {
        get { lock (Gate) return _registered; }
    }

    public static void Register(IRetentionGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        lock (Gate) _registered = _registered.Append(gate).ToArray();
    }

    public static void Unregister(IRetentionGate gate)
    {
        lock (Gate) _registered = _registered.Where(g => !ReferenceEquals(g, gate)).ToArray();
    }

    /// <summary>True when every gate allows; a throwing gate refuses.</summary>
    internal static bool AllAllow(IEnumerable<IRetentionGate> gates, RetentionCandidate candidate, Action<string>? log)
    {
        foreach (var g in gates)
        {
            try
            {
                if (!g.AllowsDelete(candidate)) return false;
            }
            catch (Exception ex)
            {
                try { log?.Invoke("retention gate " + g.GetType().Name + " failed: " + ex.Message); } catch { }
                return false;
            }
        }
        return true;
    }
}
