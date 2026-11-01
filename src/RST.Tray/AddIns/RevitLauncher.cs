using System.Diagnostics;
using System.IO;
using RST.Core.Activation;
using RST.Tray.Resources;

namespace RST.Tray.AddIns;

/// <summary>Starts the chosen installed Revit directly, without a shell, arguments or process reuse.</summary>
internal sealed class RevitLauncher(
    Func<IReadOnlyList<RevitYear>>? installed = null,
    Func<string, bool>? fileExists = null,
    Action<ProcessStartInfo>? start = null)
{
    public void Launch(int year)
    {
        var installation = RevitInstallations.SupportedYears.Contains(year)
            ? (installed?.Invoke() ?? RevitInstallations.Detect(AddinRoots.FromEnvironment())).SingleOrDefault(x => x.Year == year)
            : null;
        if (installation?.InstallDir is not { Length: > 0 } directory || !Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException(AddInsStrings.RevitNotFound(year));

        directory = Path.GetFullPath(directory);
        var executable = Path.Combine(directory, "Revit.exe");
        if (!(fileExists ?? File.Exists)(executable))
            throw new InvalidOperationException(AddInsStrings.RevitNotFound(year));

        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = directory };
        if (start is not null) start(info);
        else
        {
            using var process = Process.Start(info);
            if (process is null) throw new InvalidOperationException(AddInsStrings.RevitNotFound(year));
        }
    }
}
