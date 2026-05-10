# RST Installer (Revit 2027 preview)

Standalone preview MSI for Revit 2027 (`RST-R27.msi`). Lives separately from `installer/RST.Installer.wixproj` until the R27 build path stabilises in CI; at that point this project is end-of-lifed and folded into the unified MSI.

For dependencies, build commands, install/uninstall, versioning, and signing — see [`../installer/README.md`](../installer/README.md). The dependency story is identical except for the .NET SDK requirement (R27 needs **.NET SDK 10.0**, not 8.0).

The two MSIs are independent products with distinct `UpgradeCode`s and coexist on disk. Once R27 lands in the unified `RST.msi`, users uninstall `RST-R27.msi` first.
