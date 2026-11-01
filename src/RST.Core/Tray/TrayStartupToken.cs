// rst-c's own: hidden starts must not inherit a third-party installer's elevated token (SC-047 R3).

namespace RST.Core.Tray;

public static class TrayStartupToken
{
    /// <summary>
    /// TokenElevationTypeFull (2) means elevated through UAC. Default (1) has no split token:
    /// even an administrator must be allowed, since sign-in gives that account the same token.
    /// Explicit launches keep their existing behaviour; only --startup is guarded (decision #40).
    /// </summary>
    public static bool ShouldExit(TrayArguments args, int elevationType, bool isSystem) =>
        args.StartHidden && (elevationType == 2 || isSystem);
}
