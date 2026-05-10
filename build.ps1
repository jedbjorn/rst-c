# build.ps1 — clean reinstall of both RST MSIs from a fresh checkout.
#
# Sequence:
#   1. Refuse to run if Revit is open (DLL locks would foil uninstall + install).
#   2. Uninstall any existing RST* products under HKCU (per-user MSIs).
#   3. Stage R25, R26, R27 via build/stage.sh.
#   4. Build the unified RST.msi (R25 + R26) and the standalone RST-R27.msi.
#   5. Install both MSIs silently.
#
# Usage:
#   .\build.ps1                   # Release config; full uninstall→build→install
#   .\build.ps1 -Config Debug     # Debug build for VM diagnostics
#   .\build.ps1 -SkipUninstall    # leave any existing install in place
#   .\build.ps1 -SkipInstall      # build only — don't touch the running install

[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Config = 'Release',
    [switch]$SkipUninstall,
    [switch]$SkipInstall
)

$ErrorActionPreference = 'Stop'

# 0. Revit must be closed; otherwise %AppData%\RST\R<NN>\app\*.dll are locked
#    and both uninstall and install will fail.
if (Get-Process -Name 'Revit' -ErrorAction SilentlyContinue) {
    throw "Revit is running. Close it before re-running this script."
}

$bash = 'C:\Program Files\Git\bin\bash.exe'
if (-not (Test-Path $bash)) {
    throw "Git bash not found at $bash. Install Git for Windows (with Git Bash) or edit `$bash in this script."
}

$repoRoot = $PSScriptRoot
Push-Location $repoRoot
try {
    # 1. Uninstall any existing RST products. Per-user MSIs register under
    #    HKCU; walk that key, match by DisplayName, and msiexec /x each.
    if (-not $SkipUninstall) {
        Write-Host "==> uninstalling existing RST installs (if any)" -ForegroundColor Cyan
        $uninstallRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
        $existing = Get-ChildItem -Path $uninstallRoot -ErrorAction SilentlyContinue |
                    Where-Object {
                        $name = (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).DisplayName
                        $name -match '^RST'
                    }
        if (-not $existing) {
            Write-Host "    (none installed)"
        } else {
            foreach ($k in $existing) {
                $productCode = $k.PSChildName
                $name = (Get-ItemProperty $k.PSPath).DisplayName
                Write-Host "    msiexec /x $productCode  ($name)"
                $proc = Start-Process msiexec -Wait -PassThru -ArgumentList '/x', $productCode, '/qn'
                if ($proc.ExitCode -ne 0) {
                    Write-Warning "uninstall of $name returned exit code $($proc.ExitCode) — continuing"
                }
            }
        }
    }

    # 2. Stage every Revit major.
    foreach ($major in 'R25', 'R26', 'R27') {
        Write-Host "==> stage $major $Config" -ForegroundColor Cyan
        & $bash 'build/stage.sh' $major $Config
        if ($LASTEXITCODE -ne 0) { throw "stage $major failed (exit $LASTEXITCODE)" }
    }

    # 3. Build both MSIs.
    Write-Host "==> build unified MSI (R25 + R26)" -ForegroundColor Cyan
    dotnet build 'installer\RST.Installer.wixproj' -c $Config --nologo
    if ($LASTEXITCODE -ne 0) { throw "unified MSI build failed (exit $LASTEXITCODE)" }

    Write-Host "==> build R27 standalone MSI" -ForegroundColor Cyan
    dotnet build 'installer-r27\RST.Installer.R27.wixproj' -c $Config --nologo
    if ($LASTEXITCODE -ne 0) { throw "R27 MSI build failed (exit $LASTEXITCODE)" }

    $unifiedMsi = "installer\bin\$Config\RST.msi"
    $r27Msi = "installer-r27\bin\$Config\RST-R27.msi"

    # 4. Install both MSIs.
    if (-not $SkipInstall) {
        Write-Host "==> install $unifiedMsi" -ForegroundColor Cyan
        $proc = Start-Process msiexec -Wait -PassThru -ArgumentList '/i', $unifiedMsi, '/qn'
        if ($proc.ExitCode -ne 0) { throw "install of $unifiedMsi failed (exit $($proc.ExitCode))" }

        Write-Host "==> install $r27Msi" -ForegroundColor Cyan
        $proc = Start-Process msiexec -Wait -PassThru -ArgumentList '/i', $r27Msi, '/qn'
        if ($proc.ExitCode -ne 0) { throw "install of $r27Msi failed (exit $($proc.ExitCode))" }
    }

    Write-Host ""
    Write-Host "==> done." -ForegroundColor Green
    Write-Host "    $unifiedMsi"
    Write-Host "    $r27Msi"
}
finally {
    Pop-Location
}
