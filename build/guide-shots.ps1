<#
.SYNOPSIS
    Makes the user guide's pictures again (S28, #4), from free-sim data only: docs/user-guide/images/*.png.

.DESCRIPTION
    - graph.png and graph-by-delay.png: `stringline render-png` on Waterloo's 4x recording.
    - The app's windows (main-window, grips, connect, replay, options, profile-editor, timetable-editor): the App tests in
      GuideScreenshotTests, run with STRINGLINE_WINDOW_SHOTS set to a scratch folder (Windows only, as they show WPF windows).

    Run it after a change to the app's look, then check the pictures and commit them.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$images = Join-Path $root 'docs/user-guide/images'
$fixtures = Join-Path $root 'tests/fixtures'
$wtt = Join-Path $fixtures 'waterloo 2010.WTT'
$log = Join-Path $fixtures 'waterloo-4x.sslog'
New-Item -ItemType Directory -Force $images | Out-Null

Push-Location $root
try {
    # The graph as the CLI draws it: three hours of Waterloo's main corridor, with the actual running up to 08:00.
    $render = @('run', '--project', 'src/Stringline.Tools', '-c', $Configuration, '--', 'render-png',
        '--wtt', $wtt, '--log', $log, '--corridor', 'main', '--at', '08:00', '--hours', '3', '--width', '1400', '--height', '800')
    & dotnet @render (Join-Path $images 'graph.png')
    if ($LASTEXITCODE -ne 0) { throw "render-png failed ($LASTEXITCODE)" }
    & dotnet @render '--colour' 'delay' (Join-Path $images 'graph-by-delay.png')
    if ($LASTEXITCODE -ne 0) { throw "render-png --colour delay failed ($LASTEXITCODE)" }

    # The app's windows, saved by the tests.
    $shots = Join-Path ([System.IO.Path]::GetTempPath()) "stringline-guide-shots-$PID"
    New-Item -ItemType Directory -Force $shots | Out-Null
    $env:STRINGLINE_WINDOW_SHOTS = $shots
    try {
        & dotnet test tests/Stringline.App.Tests -c $Configuration --filter 'FullyQualifiedName~GuideScreenshotTests'
        if ($LASTEXITCODE -ne 0) { throw "GuideScreenshotTests failed ($LASTEXITCODE)" }
    }
    finally {
        Remove-Item Env:STRINGLINE_WINDOW_SHOTS
    }

    $made = Get-ChildItem $shots -Filter 'guide-*.png'
    if ($made.Count -eq 0) { throw "The tests saved no pictures in $shots" }
    foreach ($shot in $made) {
        Copy-Item $shot.FullName (Join-Path $images ($shot.Name -replace '^guide-', ''))
    }
    Remove-Item -Recurse -Force $shots

    Get-ChildItem $images -Filter '*.png' | ForEach-Object { '{0,-24} {1,8:N0} KB' -f $_.Name, ($_.Length / 1KB) }
}
finally {
    Pop-Location
}
