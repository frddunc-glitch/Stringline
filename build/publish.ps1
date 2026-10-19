<#
.SYNOPSIS
    Publishes Stringline for Windows: a single-file, self-contained Stringline.exe, a zip of it with the licence and
    notices, and (with -Installer) a setup program made with Inno Setup.

.EXAMPLE
    ./build/publish.ps1                      # artifacts/Stringline-0.1.0-win-x64.zip
    ./build/publish.ps1 -Installer           # also artifacts/Stringline-0.1.0-setup.exe (needs Inno Setup 6)
    ./build/publish.ps1 -Version 0.2.0 -Installer

    The exe, installer and uninstaller are code-signed by build/sign.ps1 when STRINGLINE_SIGN_PFX names a certificate
    (see that script); without one they are left unsigned, with a warning.
#>
[CmdletBinding()]
param(
    # The version to stamp; defaults to <Version> in Directory.Build.props.
    [string] $Version,
    # Also build the installer (needs Inno Setup 6's ISCC.exe on the PATH, in Program Files, or given by -Iscc).
    [switch] $Installer,
    [string] $Iscc,
    # Where the zip, installer and publish folder go.
    [string] $Output = 'artifacts'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if (-not $Version) {
        $Version = ([xml](Get-Content 'Directory.Build.props')).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    }
    if ($Version -notmatch '^\d+\.\d+\.\d+([-+].*)?$') { throw "Not a version: '$Version'." }
    $plain = ($Version -split '[-+]')[0]

    $out = [IO.Path]::GetFullPath((Join-Path $root $Output))
    $app = Join-Path $out 'publish\Stringline'
    if (Test-Path $app) { Remove-Item $app -Recurse -Force }
    New-Item -ItemType Directory -Force $app | Out-Null

    Write-Host "Publishing Stringline $Version (single-file, self-contained, win-x64)..."
    dotnet publish src/Stringline.App/Stringline.App.csproj -c Release -r win-x64 `
        -p:PublishSingleFile=true -p:Version=$Version -o $app --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

    # Only the exe and the notices ship; the fonts are embedded in the exe.
    Get-ChildItem $app -Exclude 'Stringline.exe' | Remove-Item -Recurse -Force
    $sign = Join-Path $PSScriptRoot 'sign.ps1'
    & $sign (Join-Path $app 'Stringline.exe')
    Copy-Item LICENSE (Join-Path $app 'LICENSE.txt')
    Copy-Item THIRD-PARTY-NOTICES.md (Join-Path $app 'THIRD-PARTY-NOTICES.md')
    Copy-Item src/Stringline.Graph/Fonts/OFL.txt (Join-Path $app 'OFL.txt')
    Copy-Item README.md (Join-Path $app 'README.md')

    $zip = Join-Path $out "Stringline-$Version-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $app '*') -DestinationPath $zip
    Write-Host "Wrote $zip"

    if ($Installer) {
        if (-not $Iscc) {
            $Iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
        }
        if (-not $Iscc) {
            $Iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
                Where-Object { Test-Path $_ } | Select-Object -First 1
        }
        if (-not $Iscc) { throw 'Inno Setup 6 (ISCC.exe) was not found. Install it (winget install JRSoftware.InnoSetup) or pass -Iscc.' }

        # Inno signs the installer and its uninstaller through sign.ps1 when there is a certificate.
        $signing = @()
        if ($env:STRINGLINE_SIGN_PFX) {
            $signing = @("/Sstringline=powershell.exe -NoProfile -ExecutionPolicy Bypass -File `$q$sign`$q `$f", '/DSign')
        }
        & $Iscc /Q "/DAppVersion=$Version" "/DAppNumericVersion=$plain" "/DSourceDir=$app" "/DOutputDir=$out" @signing installer/Stringline.iss
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)." }
        Write-Host "Wrote $(Join-Path $out "Stringline-$Version-setup.exe")"
    }
}
finally {
    Pop-Location
}
