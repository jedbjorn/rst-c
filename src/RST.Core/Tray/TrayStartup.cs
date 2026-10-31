// rst-c's own (not vendored). SC-046 (T2): TrayHost.Start ran its steps (settings, theme, icon, the update checker,
// installs, the show signal, the window) one after another with nothing around them. A step that threw stopped
// the rest, and the dispatcher's handler kept the process alive: the tray held its single-instance mutex with no
// icon, so the user could neither see it nor start another. The steps now run here: the icon first, a failed step
// is logged and the tray runs without that feature, and only a failure of a step the tray cannot run without ends
// the process (with a non-zero code, releasing the mutex).

using System;
using System.Collections.Generic;
using RST.Core.Logging;

namespace RST.Core.Tray;

/// <summary>One step of the tray's start.</summary>
/// <param name="Name">For the log: "icon", "update checks".</param>
/// <param name="Run">The work.</param>
/// <param name="Essential">The tray cannot run without it: a failure ends the start.</param>
public sealed record TrayStartStep(string Name, Action Run, bool Essential = false);

/// <summary>Runs the tray's start steps in order (TrayHost.Start).</summary>
public static class TrayStartup
{
    /// <summary>The tray's exit code when it could not start (an essential step failed).</summary>
    public const int FatalExitCode = 2;

    internal const string DegradedTemplate = "tray start: {Step} failed; the tray runs without it";

    internal const string FatalTemplate = "tray start: {Step} failed; the tray cannot run without it and exits";

    /// <summary>
    /// Runs each step in order. A step that throws is logged at Error and the next one runs; an essential step
    /// that throws is logged at Fatal and nothing after it runs. Returns the failed essential step's name, or
    /// null when the tray is up (perhaps without some features). Never throws.
    /// </summary>
    public static string? Run(IReadOnlyList<TrayStartStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        foreach (var step in steps)
        {
            try
            {
                step.Run();
            }
            catch (Exception ex)
            {
                if (step.Essential)
                {
                    Log.Fatal(ex, FatalTemplate, step.Name);
                    return step.Name;
                }
                Log.Error(ex, DegradedTemplate, step.Name);
            }
        }
        return null;
    }
}
