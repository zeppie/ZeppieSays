<#
.SYNOPSIS
    Builds the ZeppieSays installer (Setup.exe) and, with -Publish, puts it on GitHub so installed copies update.

.DESCRIPTION
    Publishes the app, packs it with Velopack into artifacts\releases, and with -Publish uploads that release to
    https://github.com/zeppie/ZeppieSays/releases. Installed copies check those releases and update themselves.
    Each release needs a higher version than the last one.

.EXAMPLE
    .\tools\release.ps1 0.2.0            # build only: try artifacts\releases\Zeppie.ZeppieSays-win-Setup.exe first
.EXAMPLE
    .\tools\release.ps1 0.2.0 -Publish   # build and upload; installed copies pick it up within a few hours
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publishDir = Join-Path $root 'artifacts\publish'
$releasesDir = Join-Path $root 'artifacts\releases'
$repo = 'https://github.com/zeppie/ZeppieSays'

function Invoke-Step([string]$name, [scriptblock]$step) {
    Write-Host "== $name" -ForegroundColor Cyan
    & $step
    if ($LASTEXITCODE) { throw "$name failed (exit code $LASTEXITCODE)" }
}

Push-Location $root
try {
    Invoke-Step 'Restore tools' { dotnet tool restore }

    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    Invoke-Step "Publish $Version" {
        dotnet publish src/ZeppieSays -c Release -r win-x64 --self-contained false "-p:Version=$Version" -o $publishDir
    }

    $token = gh auth token
    if ($LASTEXITCODE -or -not $token) { throw 'Log in to GitHub first: gh auth login' }

    # The latest published release, so vpk can add a small delta update next to the full package.
    Write-Host '== Download the previous release' -ForegroundColor Cyan
    dotnet vpk download github --repoUrl $repo --token $token -o $releasesDir
    if ($LASTEXITCODE) { Write-Host 'No previous release found; this one only gets a full package.' }

    Invoke-Step "Pack $Version" {
        dotnet vpk pack --packId Zeppie.ZeppieSays --packVersion $Version --packDir $publishDir --runtime win-x64 `
            --mainExe ZeppieSays.exe --packTitle ZeppieSays --packAuthors 'Dirk Vaessen' `
            --icon src/ZeppieSays/Assets/zeppie.ico --framework net10.0-x64-desktop -o $releasesDir
    }

    if ($Publish) {
        Invoke-Step "Upload $Version to GitHub" {
            dotnet vpk upload github --repoUrl $repo --token $token -o $releasesDir `
                --publish --releaseName "ZeppieSays $Version" --tag "v$Version"
        }
        Write-Host "Published: $repo/releases/tag/v$Version" -ForegroundColor Green
    }
    else {
        Write-Host "Built: $releasesDir\Zeppie.ZeppieSays-win-Setup.exe (run with -Publish to upload)" -ForegroundColor Green
    }
}
finally {
    Pop-Location
}
