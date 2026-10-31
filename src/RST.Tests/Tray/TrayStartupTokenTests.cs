// rst-c's own: hidden starts after third-party Restart Manager sessions (SC-047 R3).

using RST.Core.Tray;
using Xunit;

namespace RST.Tests.Tray;

public class TrayStartupTokenTests
{
    [Theory]
    [InlineData(1, false, false)] // no split token, including an administrator with UAC off
    [InlineData(2, false, true)]  // full UAC token
    [InlineData(3, false, false)] // limited UAC token
    [InlineData(1, true, true)]   // LocalSystem has no split token either
    [InlineData(2, true, true)]
    [InlineData(3, true, true)]
    public void Only_hidden_starts_with_a_full_UAC_token_or_as_SYSTEM_exit(int type, bool system, bool exit)
    {
        Assert.Equal(exit, TrayStartupToken.ShouldExit(TrayArguments.Parse(new[] { "--startup" }), type, system));
        Assert.Equal(exit, TrayStartupToken.ShouldExit(TrayArguments.Parse(new[] { "--STARTUP" }), type, system));
        Assert.False(TrayStartupToken.ShouldExit(TrayArguments.Parse(new string[0]), type, system));
    }
}
