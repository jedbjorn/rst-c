// Decision #73: tray installs advise closing Revit. Windows Installer must not close it for the user. Spec #38: Remove
// runs System32's msiexec /x on the verified ProductCode the same way.
using System.IO;
using RST.Tray.Updates;
using Xunit;

namespace RST.RuntimeTests.Tray;

public class InstallerArgumentsTests
{
    [Fact]
    public void Direct_MSI_disables_Restart_Manager_and_never_restarts_Windows()
    {
        var start = Msiexec.InstallStartInfo(@"C:\cache with spaces\app.msi", @"C:\logs\install.log");
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal(new[] { "/i", @"C:\cache with spaces\app.msi", "/qn", "/norestart",
            "MSIRESTARTMANAGERCONTROL=Disable", "/l*v", @"C:\logs\install.log" }, start.ArgumentList);
    }

    [Fact]
    public void Remove_runs_System32_msiexec_x_with_Restart_Manager_disabled_and_never_restarts_Windows()
    {
        const string code = "{0B4C5C1E-2D7A-4F7E-9C1D-3A6B8E9F0A12}";
        var start = Msiexec.UninstallStartInfo(code, @"C:\logs with spaces\remove-drive-it.log");
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal(new[] { "/x", code, "/qn", "/norestart", "/l*v", @"C:\logs with spaces\remove-drive-it.log",
            "MSIRESTARTMANAGERCONTROL=Disable" }, start.ArgumentList);
    }

    [Fact]
    public void Bundle_update_uses_the_authored_Restart_Manager_variable_and_no_companion_flags()
    {
        var start = Msiexec.BundleStartInfo(@"C:\cache\RST-Setup.exe", @"C:\logs\bundle.log");
        Assert.Equal(@"C:\cache\RST-Setup.exe", start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal(new[] { "/quiet", "/norestart", "RstRestartManagerControl=Disable", "/log", @"C:\logs\bundle.log" }, start.ArgumentList);
    }
}
