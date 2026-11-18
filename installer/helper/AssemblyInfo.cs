// AssemblyInfo.cs: the version resource (Explorer's Properties -> Details) of the three installer helpers built with the
// .NET Framework 4 compiler (feature #10, spec #12): RST.StartTray.exe and RST.EndTrays.exe (installer/RST.Installer.wixproj,
// the MSIs' Binary table) and RST.RemoveUserRst.exe (installer-bundle/RST.Setup.wixproj, RST-Setup.exe). Tag releases sign
// them; like every RST binary they name Designs/OS as the company (Directory.Build.props). Each build passes the define
// that picks its description.
using System.Reflection;

[assembly: AssemblyCompany("Designs/OS")]
[assembly: AssemblyProduct("RST-C")]
[assembly: AssemblyCopyright("© 2026 Designs/OS")]
#if RST_HELPER_STARTTRAY
[assembly: AssemblyTitle("RST tray start (installer helper)")]
#elif RST_HELPER_ENDTRAYS
[assembly: AssemblyTitle("RST tray close (installer helper)")]
#elif RST_HELPER_REMOVEUSERRST
[assembly: AssemblyTitle("RST per-user copy removal (setup helper)")]
#endif
