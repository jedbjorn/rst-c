---
rendered_by: super-coder
source: db
edit: changes here are overwritten — author via the shell or localhost GUI
feature: 
roadmap_status: 
frozen: false
---

# MSI Release Runbook (RST.msi / RST-R27.msi)

How to cut a GitHub Release with the branded installers attached. Source of
truth in-repo: `installer/README.md` (Versioning + Release flow) and
`.github/workflows/release.yml`.

## Mechanism
- **Tag → Release.** Pushing a tag matching `v*` to GitHub triggers
  `.github/workflows/release.yml`. Pushes to `main` do NOT auto-tag or release.
- `release.yml` calls the reusable `_build.yml` (the exact same pipeline the
  PR/`build.yml` check runs): test → stage R25/R26/R27 (Linux) → build MSIs on
  a **Windows** runner → then a release job attaches `RST.msi` (R25+R26) and
  `RST-R27.msi` (R27, soft-fail) to a new GitHub Release with auto-generated
  notes.
- Hyphenated tags (`v1.1.1-rc.1`, `v1.2.0-beta`) are flagged **pre-release**.

## The one gotcha — bump the version FIRST
The tag is only the Release *name*; it is NOT threaded into the build. The
installed product version comes from `<Package Version="x.y.z">` in BOTH
`installer/Product.wxs` and `installer-r27/Product.wxs` (keep them in lockstep).
If you forget to bump, the Release is named `v1.1.1` while installed copies
still report the old version. So: bump the WXS Version, merge, THEN tag.

## Steps
1. Branch, bump `<Package Version="…">` in **both** Product.wxs files, open PR,
   get it merged to `main` (merge is the FnB's gate).
2. From `main` HEAD:
   ```
   git tag v1.1.1 && git push origin v1.1.1
   ```
3. Watch the run: `gh run watch <id>` — confirm `MSI (R25 + R26)` is green.
   R27 (`msi-r27`) is `continue-on-error`; it may be absent without failing
   the release.
4. The Release lands at `releases/tag/v1.1.1` with the MSI(s) attached.

## Notes / invariants
- **UpgradeCode is immutable** — never change the GUIDs in either Product.wxs
  once shipped (RST.msi: AF0331FC-…, RST-R27.msi: 02BE4399-…). `<MajorUpgrade>`
  handles in-place upgrade; changing the code orphans installs in Add/Remove.
- WiX MSI emission is **Windows-only**; staging runs on Linux, only the WiX
  step pays the Windows-runner cost. There is no `dotnet`/`wix` in the sandbox
  — never claim a local MSI build; verify via CI.
- Repo uses **Central Package Management**: installer package versions (e.g.
  `WixToolset.UI.wixext`) live in `Directory.Packages.props` as `PackageVersion`;
  a `PackageReference` with a `Version=` trips NU1008.
- Branding assets the build consumes live in `installer/branding/`
  (banner.bmp / dialog.bmp / License.rtf / rst.ico), shared with installer-r27
  via `..\installer\branding\`.
