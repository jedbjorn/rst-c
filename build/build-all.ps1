# build-all.ps1 — stage every Revit major and build both MSIs.
#
# Run from anywhere; the script cd's to the repo root automatically.
#   .\build\build-all.ps1                   # Release (default)
#   .\build\build-all.ps1 -Config Debug
#
# Outputs:
#   installer\bin\<Config>\RST.msi          (R25 + R26 unified)
#   installer-r27\bin\<Config>\RST-R27.msi  (R27 standalone)

[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Config = 'Release'
)

$ErrorActionPreference = 'Stop'

$bash = 'C:\Program Files\Git\bin\bash.exe'
if (-not (Test-Path $bash)) {
    throw "Git bash not found at $bash. Install Git for Windows (with Git Bash) or edit this script's `$bash path."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    foreach ($major in 'R25', 'R26', 'R27') {
        Write-Host "==> stage $major $Config" -ForegroundColor Cyan
        & $bash 'build/stage.sh' $major $Config
        if ($LASTEXITCODE -ne 0) { throw "stage $major failed (exit $LASTEXITCODE)" }
    }

    Write-Host "==> build unified MSI (R25 + R26)" -ForegroundColor Cyan
    dotnet build 'installer\RST.Installer.wixproj' -c $Config --nologo
    if ($LASTEXITCODE -ne 0) { throw "unified MSI build failed (exit $LASTEXITCODE)" }

    Write-Host "==> build R27 standalone MSI" -ForegroundColor Cyan
    dotnet build 'installer-r27\RST.Installer.R27.wixproj' -c $Config --nologo
    if ($LASTEXITCODE -ne 0) { throw "R27 MSI build failed (exit $LASTEXITCODE)" }

    Write-Host ""
    Write-Host "==> done. MSIs:" -ForegroundColor Green
    Write-Host "    installer\bin\$Config\RST.msi"
    Write-Host "    installer-r27\bin\$Config\RST-R27.msi"
}
finally {
    Pop-Location
}
