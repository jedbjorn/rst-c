// Decision #73: tray installs advise closing Revit. Windows Installer must not close it for the user.
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
    public void Bundle_update_uses_the_authored_Restart_Manager_variable_and_no_companion_flags()
    {
        var start = Msiexec.BundleStartInfo(@"C:\cache\RST-Setup.exe", @"C:\logs\bundle.log");
        Assert.Equal(@"C:\cache\RST-Setup.exe", start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal(new[] { "/quiet", "/norestart", "RstRestartManagerControl=Disable", "/log", @"C:\logs\bundle.log" }, start.ArgumentList);
    }
}
