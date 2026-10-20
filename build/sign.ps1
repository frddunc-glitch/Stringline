<#
.SYNOPSIS
    Authenticode-signs files with signtool when a code-signing certificate is set up (#1), and otherwise skips them
    with a warning. publish.ps1 calls it for Stringline.exe, and Inno Setup calls it for the installer and uninstaller.

.DESCRIPTION
    The certificate comes only from environment variables, so it never appears on a command line or in a log:
      STRINGLINE_SIGN_PFX           path to the .pfx file (in CI, decoded from a GitHub secret to a temporary file)
      STRINGLINE_SIGN_PASSWORD      its password (optional for a .pfx without one)
      STRINGLINE_SIGN_TIMESTAMP     RFC 3161 timestamp server (default http://timestamp.digicert.com)
    With no STRINGLINE_SIGN_PFX the files are left unsigned and the script succeeds. Windows then says
    "Unknown publisher" (see docs/developer.md).

.EXAMPLE
    ./build/sign.ps1 artifacts/publish/Stringline/Stringline.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string[]] $Path,
    # signtool.exe to use; found on the PATH or in the Windows SDK if not given.
    [string] $SignTool
)

$ErrorActionPreference = 'Stop'

$pfx = $env:STRINGLINE_SIGN_PFX
if (-not $pfx) {
    Write-Warning "Not signing $($Path -join ', '): no code-signing certificate is set up (STRINGLINE_SIGN_PFX). Windows will show 'Unknown publisher'."
    exit 0
}
if (-not (Test-Path -LiteralPath $pfx -PathType Leaf)) { throw 'The code-signing certificate named by STRINGLINE_SIGN_PFX does not exist.' }

if (-not $SignTool) {
    $SignTool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
}
if (-not $SignTool) {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $SignTool = Get-ChildItem $kits -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object { $_.Directory.Parent.Name } -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $SignTool) { throw 'signtool.exe was not found. Install the Windows SDK or pass -SignTool.' }

$timestamp = if ($env:STRINGLINE_SIGN_TIMESTAMP) { $env:STRINGLINE_SIGN_TIMESTAMP } else { 'http://timestamp.digicert.com' }
foreach ($file in $Path) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Nothing to sign at $file." }
    $arguments = @('sign', '/fd', 'SHA256', '/td', 'SHA256', '/tr', $timestamp, '/d', 'Stringline',
        '/du', 'https://github.com/frddunc-glitch/Stringline', '/f', $pfx)
    if ($env:STRINGLINE_SIGN_PASSWORD) { $arguments += @('/p', $env:STRINGLINE_SIGN_PASSWORD) }
    # signtool's output names the file and the certificate, never the password; the arguments are not echoed.
    & $SignTool @arguments $file
    if ($LASTEXITCODE -ne 0) { throw "signtool could not sign $file ($LASTEXITCODE)." }
    Write-Host "Signed $file"
}
