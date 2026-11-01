using System.Diagnostics;
using System.IO;
using RST.Core.Activation;
using RST.Tray.AddIns;
using Xunit;

namespace RST.RuntimeTests.Tray;

public class RevitLauncherTests
{
    [Fact]
    public void Starts_only_the_selected_installed_executable_without_arguments_or_shell()
    {
        ProcessStartInfo? started = null;
        var directory = Path.Combine(Path.GetTempPath(), "Custom Revit 2026");
        var executable = Path.Combine(directory, "Revit.exe");
        var launcher = new RevitLauncher(
            () => [new RevitYear(2025, @"C:\Other Revit"), new RevitYear(2026, directory)],
            path => path == executable, info => started = info);

        launcher.Launch(2026);

        Assert.NotNull(started);
        Assert.Equal(executable, started.FileName);
        Assert.Equal(directory, started.WorkingDirectory);
        Assert.False(started.UseShellExecute);
        Assert.Empty(started.Arguments);
        Assert.Empty(started.ArgumentList);
        Assert.Empty(started.Verb);
    }

    [Theory]
    [InlineData(2024, @"C:\Revit", true)]
    [InlineData(2027, @"C:\Revit", true)]
    [InlineData(2026, null, true)]
    [InlineData(2026, "relative", true)]
    [InlineData(2026, @"C:\Revit", false)]
    public void Missing_unsupported_or_relative_installations_do_not_start(int year, string? directory, bool exists)
    {
        var started = false;
        var launcher = new RevitLauncher(() => [new RevitYear(2026, directory)], _ => exists, _ => started = true);

        Assert.Throws<InvalidOperationException>(() => launcher.Launch(year));

        Assert.False(started);
    }
}
