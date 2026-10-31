// rst-c's own (not vendored): a rollup's model identity (spec #24, Rollup
// Store, "Model identity"). A model is keyed at the highest ladder level its
// doc_opened identity carries — cloud pair → central GUID → central path →
// creation GUID — and model_id is a stable hash of that level and its
// case-folded value, so a rebuild yields the same id and all local copies of
// one central roll up to one model.

using System.Security.Cryptography;
using System.Text;

namespace RST.Core.Telemetry.Rollups;

public readonly record struct ModelKey(string Kind, string Value)
{
    /// <summary>Ladder rank: 0 = cloud pair (highest) … 3 = creation GUID.</summary>
    public int Rank => Kind switch
    {
        ActivityMatchKinds.Cloud => 0,
        ActivityMatchKinds.Central => 1,
        ActivityMatchKinds.CentralPath => 2,
        _ => 3,
    };

    /// <summary>32 lowercase hex characters: the first 16 bytes of SHA-256("kind:VALUE").</summary>
    public string ModelId
    {
        get
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Kind + ":" + Value));
            return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
        }
    }

    /// <summary>The key a doc_opened identity defines, or null when it carries no ladder key
    /// (nothing can match it, so it has no rollup).</summary>
    public static ModelKey? From(DocumentIdentity id)
    {
        if (Norm(id.CloudProjectGuid) is { } project && Norm(id.CloudModelGuid) is { } model)
            return new ModelKey(ActivityMatchKinds.Cloud, project + "/" + model);
        if (Norm(id.CentralGuid) is { } central)
            return new ModelKey(ActivityMatchKinds.Central, central);
        if (Norm(id.CentralPath) is { } path)
            return new ModelKey(ActivityMatchKinds.CentralPath, path);
        if (Norm(id.CreationGuid) is { } creation)
            return new ModelKey(ActivityMatchKinds.Creation, creation);
        return null;
    }

    /// <summary>True when <paramref name="modelId"/> has the shape <see cref="ModelId"/> produces
    /// (it is used as a file name, so nothing else is accepted).</summary>
    public static bool IsValidId(string? modelId) =>
        modelId is { Length: 32 } && modelId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string? Norm(string? v) =>
        string.IsNullOrWhiteSpace(v) ? null : v.Trim().ToUpperInvariant();
}
