// RstifyVisibilityRules.cs — share the drift gate between ribbon notifications
// and the Idling backstop so neither fights a lifted rule or hides an escape hatch.

namespace RST.Core.Profiles;

public static class RstifyVisibilityRules
{
    public static bool ShouldRehide(IReadOnlyCollection<string> hiddenTitles, string? title,
        bool isVisible, bool isSelfMutation, bool isProtectedTab) =>
        !isSelfMutation && isVisible && !isProtectedTab && title is not null
        && hiddenTitles.Contains(title, StringComparer.Ordinal);
}
