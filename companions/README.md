# Companion add-ins

Designs/OS builds other Revit add-ins beside rst-c. Each one lives in its own repo and ships its own MSI. `companions.json` in this folder is the checked-in list of those **companions**: which ones rst-c knows about, and, for the ones it ships, the exact release it ships.

The setup bundle (`RST-Setup.exe`, feature #13 Phase 2) is generated from this file. It always installs rst-c, and offers each `shippable` companion as a checkbox. Ticking one runs that companion's own MSI silently. After that, every product is a stock install: its own Add/Remove Programs entry, its own upgrades, no runtime link to rst-c. Uninstalling rst-c leaves the companions installed.

This is **not** `src/RST.UI/Assets/lookup/addin_lookup.json`. That file is a runtime lookup the Builder uses for download links on a profile's required add-ins. This registry is used at build time. From v3.1 one part of it is also used at runtime: RST.Core embeds the file when it is built, and the RST tray reads only each shippable companion's identity fields (`id`, `name`, `description`, `repo`, `release.upgradeCode`, `addinClientId`) to detect it and check its latest release ([`docs/tray.md`](../docs/tray.md#updates)). The pins (`tag`, `asset`, `sha256`, `version`) stay build-time only: the bundle installs the pinned release, and the tray follows each repo's latest full release.

| Companion | Repo | Status | Pin |
|---|---|---|---|
| Gantt-It | [`jedbjorn/gantt-it`](https://github.com/jedbjorn/gantt-it) | `shippable` | `v1.1.0`, `GanttIt-1.1.0.msi` |
| Splash-It | [`jedbjorn/splash-it`](https://github.com/jedbjorn/splash-it) | `shippable` | `v1.1.0`, `SplashIt-1.1.0.msi` |

How the bundle is built and tested: [`installer-bundle/README.md`](../installer-bundle/README.md).

## The registry

`companions.json` is strict JSON (no comments), edited by hand in a PR. This example is the Gantt-It entry as it is pinned:

```json
{
  "schemaVersion": 1,
  "companions": [
    {
      "id": "gantt-it",
      "name": "Gantt-It",
      "description": "Gantt charts as native Revit legend views",
      "repo": "jedbjorn/gantt-it",
      "status": "shippable",
      "revitYears": [2025, 2026, 2027],
      "addinClientId": "9920fb42-542b-4ce1-9659-a078489af6b0",
      "vendorId": "DSOS",
      "defaultSelected": false,
      "release": {
        "tag": "v1.1.0",
        "asset": "GanttIt-1.1.0.msi",
        "sha256": "06f403468ae484526dc7e8ddc9251bf2b34715ddcb14d668049b63c74d1eb18b",
        "upgradeCode": "644FCAD5-E631-4441-9A11-75F175565136",
        "version": "1.1.0"
      }
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `schemaVersion` | Always `1`. |
| `id` | Unique, kebab-case, never changed. The bundle's install variable comes from it (`gantt-it` → `InstallGanttIt`). |
| `name`, `description` | The checkbox label and its one-line description in the setup. Users who have never heard of the add-in read these. |
| `repo` | `owner/name` on GitHub. The release is downloaded from here at build time. |
| `status` | `in_development`: tracked, not offered. `shippable`: offered by the bundle. `retired`: no longer offered; the entry stays for history. |
| `revitYears` | Informational (labels and docs). The companion's MSI decides what it installs. |
| `addinClientId` | The ClientId in the companion's `.addin`. `null` until known; required when `shippable`. |
| `vendorId` | The VendorId in the companion's `.addin`. Must be `DSOS` when `shippable` (C6). |
| `defaultSelected` | Whether the checkbox starts ticked. `false` for every companion: installing one is opt-in. |
| `release` | The pinned release, or `null`. Required when `shippable`; when present it must be complete: `tag`, `asset` (an `.msi`), `sha256` (64 lowercase hex), `upgradeCode` (GUID), `version`. |

Every field is present on every entry (use `null` where a value is not known yet). Unknown fields are rejected, so a misspelt field fails the build instead of being ignored.

### Validation

`src/RST.Tests/Companions/CompanionRegistryTests.cs` runs on every PR (Linux CI, the `test` job). It fails if:

- the file is not valid JSON, has an unknown or missing field, or a field has the wrong type;
- an `id` is not kebab-case or is used twice (or a `name` is used twice);
- a `shippable` entry has no complete `release`, no `addinClientId`, or a VendorId other than `DSOS`;
- any `upgradeCode` or `addinClientId` is rst-c's own (`RST.msi`'s `AF0331FC-5280-492A-94BD-95EDF108BE74`, `RST-R27.msi`'s `02BE4399-A563-4B15-BD12-ECF5D8486FB3`, or the UpgradeCode declared on the `<Package>` or `<Bundle>` of any other `.wxs` source under `installer*/` (build output in `bin/` and `obj/` is skipped), or the ClientId in `src/RST.Bootstrap/RST.addin`), or two companions share one.

Run it locally:

```sh
dotnet test src/RST.Tests/RST.Tests.csproj --filter FullyQualifiedName~Companions
```

The test checks the file's shape. It cannot check that a pin matches the real release; the CI `bundle` job does that (`installer-bundle/fetch-companions.ps1` downloads each pinned asset and fails on a missing asset or a sha256, UpgradeCode or version mismatch), and the pin-bump procedure below does it before the PR is opened.

## The companion contract

A companion repo must meet all seven before rst-c marks it `shippable`. Gantt-It and Splash-It build their installers to this.

| # | Requirement | Why |
|---|---|---|
| C1 | One **per-user** MSI (`Scope="perUser"`) covering every Revit year it supports. | The bundle stays unelevated, the same scope as `RST.msi`. |
| C2 | A **stable UpgradeCode**, never changed once shipped. | Detection and upgrade work across standalone and chained installs. |
| C3 | `MajorUpgrade` that upgrades older versions in place and **blocks downgrade**. | A newer standalone install is never replaced by an older pinned one. |
| C4 | Clean **silent** install and uninstall (`msiexec /i X.msi /qn`, `msiexec /x X.msi /qn`): exit 0, no prompts, no reboot, no UI-only properties needed. | Burn runs it silently. |
| C5 | Published as an asset on a **GitHub Release** of its repo, under the exact asset name recorded in the pin. | rst-c pins `tag` + `asset`. Asset names carry the version (`GanttIt-1.1.0.msi`), so the name is fixed per pin, not across pins. |
| C6 | Installs its own `.addin` per Revit year under `%AppData%\Autodesk\Revit\Addins\<year>\`, with its own ClientId and VendorId `DSOS`; no files under `%AppData%\RST\`. | No overlap with rst-c's files. |
| C7 | No dependency on rst-c being installed, and rst-c has none on it. | Everything stays stock after setup. |

Known behavior of the current companions, relevant to C4: both MSIs carry a launch condition that needs Revit 2025, 2026 or 2027 to be installed (`Installed OR REVIT2025FOUND OR REVIT2026FOUND OR REVIT2027FOUND`). On a machine with none of them, the silent install fails with that message instead of installing nothing. The bundle marks companions non-vital, so this never rolls back rst-c. CI runners have no Revit, so the bundle smoke creates stub `Revit.exe` files in `C:\Program Files\Autodesk\Revit <year>\` (the companions' default-folder detection, as for `RST.msi`); the install-order mechanics run against a test-only fixture MSI.

## Bumping a pin

Do this when a companion publishes a new release, or to make an `in_development` companion `shippable`. One PR per bump. The values below use Gantt-It `v1.1.0` as the example; substitute the repo and tag.

1. **Tag.** Pick a full release (not a pre-release) and record its tag.

   ```sh
   gh release list -R jedbjorn/gantt-it
   gh release view v1.1.0 -R jedbjorn/gantt-it --json tagName,isPrerelease,assets \
     -q '.tagName, .isPrerelease, (.assets[] | [.name, .digest] | @tsv)'
   ```

2. **Asset.** Record the exact name of the release's single `.msi` asset (`GanttIt-1.1.0.msi`). The per-year `.zip` files are manual-install packages and are never pinned.

3. **sha256.** Download the MSI and hash it yourself. It must equal both GitHub's `digest` from step 1 and the line in the release's `SHA256SUMS.txt` (both companions publish one). Record it in lowercase; the validation test rejects uppercase (PowerShell's `Get-FileHash` prints uppercase, so lower it first).

   ```sh
   gh release download v1.1.0 -R jedbjorn/gantt-it -p GanttIt-1.1.0.msi -p SHA256SUMS.txt -D pin/
   sha256sum pin/GanttIt-1.1.0.msi
   grep GanttIt-1.1.0.msi pin/SHA256SUMS.txt
   ```

4. **upgradeCode.** Read it from the downloaded MSI, not from memory, and check it against the companion's `installer/Product.wxs` at the tag. For an existing companion it must be **unchanged** from the previous pin (C2). If it changed, stop: the companion broke C2, and the bump does not go ahead. Record it without braces (`644FCAD5-…`, not `{644FCAD5-…}` as `msiinfo` prints it); the validation test accepts only that form.

   ```sh
   # Linux (msitools):
   msiinfo export pin/GanttIt-1.1.0.msi Property | grep -E 'UpgradeCode|ProductVersion|ALLUSERS|MSIINSTALLPERUSER'
   # Windows, from the companion's checkout at the tag: its own checker prints the identity and fails on a wrong one
   powershell -File installer\inspect-msi.ps1 -Msi pin\GanttIt-1.1.0.msi -Version 1.1.0
   git -C ../gantt-it show v1.1.0:installer/Product.wxs | grep UpgradeCode
   ```

5. **version.** Record the release's version without the `v` (`1.1.0`). It must equal the MSI's `ProductVersion` from step 4.

6. **Re-verify C1–C7 on the new pin** (next section). A pin moves only when all seven still hold.

7. **Edit `companions.json`**: set `release` to the five values, set `status` to `shippable` if it was not, and update `addinClientId`, `revitYears`, `name` and `description` if the release changed them. Run the validation test, open the PR, and put the commands and their output from steps 1–6 in the PR body.

Never change a pin's `sha256` to make the build pass. A mismatch means the upstream asset changed after it was pinned; find out why before pinning anything.

### Re-verifying C1–C7 on a new pin

Each check runs against the **downloaded asset** from step 3, not a local build of the companion.

| # | How to check |
|---|---|
| C1 | The package needs no elevation: the summary information's Word Count has bit `8` set (`msiinfo suminfo <asset>`), and `ALLUSERS` is absent from the `Property` table (or is `2` with `MSIINSTALLPERUSER=1`); never `1`. The MSI holds files for every year in `revitYears`. `inspect-msi.ps1` checks all of this. |
| C2 | `UpgradeCode` equals the previous pin's (step 4). |
| C3 | The `Upgrade` table holds exactly the two `MajorUpgrade` rows for the companion's UpgradeCode: `WIX_UPGRADE_DETECTED` removes older versions, and `WIX_DOWNGRADE_DETECTED` only detects newer versions, which the downgrade message then blocks (`msiinfo export <asset> Upgrade`). `inspect-msi.ps1` checks both rows. Both current companions also set `AllowSameVersionUpgrades="yes"`, so their removal row includes the same version too; C3 does not require that. |
| C4 | On Windows with a Revit year installed (the W10C-Testing VM, skill `rst_c_vm`): `msiexec /i <asset> /qn /l*v install.log`, then `msiexec /x <asset> /qn /l*v uninstall.log`. Both exit `0` (not `3010`, which means a reboot), and neither shows a window. |
| C5 | Step 1–3: the asset is on a full GitHub Release of `repo` under exactly the recorded name. |
| C6 | After the C4 install: the companion's manifest (`GanttIt.addin`, `SplashIt.addin`) exists in `%AppData%\Autodesk\Revit\Addins\<year>\` for each detected year, its `<ClientId>` equals `addinClientId` and its `<VendorId>` is `DSOS`; nothing was written under `%AppData%\RST\`. The validation test separately ensures the ClientId is not rst-c's. |
| C7 | The C4 install succeeds with rst-c **not** installed. Open Revit: the companion loads with no RST tab present. Then install rst-c and uninstall it through Add/Remove Programs: the companion still loads. |

**These checks are the bump author's job, every time.** rst-c's CI re-checks only part of it on the real MSIs. The `bundle` job checks that the pinned asset exists and that its sha256, UpgradeCode and ProductVersion match the pin. The `bundle-smoke (Companions)` leg installs the real companions through `RST-Setup.exe` with stub Revit folders: silent install and uninstall with exit 0 (C4), the `.addin` ClientId and VendorId per year and nothing under `%AppData%\RST\` (C6), and a standalone install with no rst-c (the install half of C7). Nothing there starts Revit, so a companion that installs cleanly but fails to load is not caught. The install-order mechanics (upgrade, newer installed, re-run) are proven on a test-only fixture MSI, not on the real companions. Acceptance check AC6 (both companions installed through `RST-Setup.exe` on the VM, then loaded in Revit) runs only on the Phase 2 PR and the v3.0.0 release candidate, not on every pin bump or release. Put the C1–C7 results for the new pin in the bump PR.
