// Copied from splash-it tests/SplashIt.RuntimeTests/Support/WpfThemeCollection.cs (splash-it commit ba86a71), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); names RstTheme; explicit Xunit using.

using Xunit;

namespace RST.RuntimeTests.Support;

/// <summary>
/// Tests that open windows under a theme. <c>RstTheme.Current</c> is process-wide (one
/// Revit UI thread in production), so these run one at a time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfThemeCollection
{
    public const string Name = "WPF theme";
}
