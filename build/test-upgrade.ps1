<#
.SYNOPSIS
    Checks that the installer updates an installed Stringline: installs an older setup, starts it with a replay
    running, installs a newer setup over it silently, then tries a downgrade and uninstalls.

.DESCRIPTION
    It checks that:
      - the upgrade closes the running app, which stops and saves its session first (apps from 0.1.1 on);
      - the files, the version in the Apps list and the install folder are the new ones, with one uninstall entry;
      - a silent older setup refuses to downgrade unless given /ALLOWDOWNGRADE (setups from 0.1.1 on);
      - the uninstaller removes the program and its entry;
      - a silent uninstall keeps the settings, profiles and logs but removes a saved password (S30), and one given
        /REMOVEUSERDATA removes them, but never Documents\Stringline.

    It installs for the current user, so it refuses to run where Stringline is already installed (unless -Force,
    which replaces that install and then uninstalls it). Your settings, profiles and logs are copied aside before the
    uninstall checks and put back afterwards, but the replay adds a session to your sessions folder. The Release
    workflow runs it on each release.

.EXAMPLE
    ./build/test-upgrade.ps1                                   # builds 0.0.1 and 0.0.2 setups, then tests them
    ./build/test-upgrade.ps1 -Old Stringline-0.1.0-setup.exe -New artifacts/Stringline-0.2.0-setup.exe
#>
[CmdletBinding()]
param(
    # The setup to install first; built from this checkout as version 0.0.1 if not given.
    [string] $Old,
    # The setup to update it with; built as version 0.0.2 if not given.
    [string] $New,
    # Replace an existing install of Stringline (it is uninstalled at the end).
    [switch] $Force,
    # Where the setup logs go.
    [string] $Output = 'artifacts/upgrade-test'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = [IO.Path]::GetFullPath((Join-Path $root $Output))
New-Item -ItemType Directory -Force $out | Out-Null
$key = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6E0B7C2A-5F1D-4C8E-9A3B-2D7F41C9E8B5}_is1'
$userKey = "HKCU:\$key"
$machineKey = "HKLM:\$key"
$appLog = Join-Path $env:LOCALAPPDATA 'Stringline\logs'
$roaming = Join-Path $env:APPDATA 'Stringline'
$local = Join-Path $env:LOCALAPPDATA 'Stringline'
$unpacked = Join-Path $env:TEMP '.net\Stringline'
$documents = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Stringline'
$backup = Join-Path $out 'userdata-backup'
$backedUp = $false
$failures = [Collections.Generic.List[string]]::new()

function Check([bool] $ok, [string] $what) {
    if ($ok) { Write-Host "  ok    $what" }
    else { Write-Host "  FAIL  $what" -ForegroundColor Red; $failures.Add($what) }
}

function Setup([string] $exe, [string] $name, [string[]] $more = @()) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/LOG=`"$(Join-Path $out "$name.log")`"") + $more
    RunBounded $exe $arguments $name
}

# Runs a setup or uninstaller and returns its exit code, or -1 if it has not finished within the timeout: then what
# it was doing is shown and saved (its log, the app's log, the windows open and a screenshot) and it is stopped, so a
# stuck installer fails the test in minutes instead of holding the Release workflow for hours.
function RunBounded([string] $exe, [string[]] $arguments, [string] $name, [int] $timeoutSeconds = 300) {
    $started = Get-Date
    $process = Start-Process $exe -ArgumentList $arguments -PassThru
    $null = $process.Handle # keeps the handle, without which ExitCode is lost once the process has gone
    if ($process.WaitForExit($timeoutSeconds * 1000)) { return $process.ExitCode }

    Write-Host "  FAIL  $name did not finish within $timeoutSeconds s" -ForegroundColor Red
    Show-Stuck $name $started
    $process.Kill($true) # with its setup.tmp child
    Get-Process Stringline -ErrorAction SilentlyContinue | Where-Object { $folder -and $_.Path -like "$folder*" } | Stop-Process -Force
    -1
}

function Show-Stuck([string] $name, [datetime] $since) {
    $log = Join-Path $out "$name.log"
    if (Test-Path $log) { Write-Host "--- last lines of $name.log"; Get-Content $log -Tail 30 | Out-Host }
    Write-Host '--- the app''s log since then'
    AppLogSince $since | Where-Object { $_ -notmatch 'Forecast of' } | Select-Object -Last 30 | Out-Host
    Write-Host '--- windows open'
    Get-Process | Where-Object { $_.MainWindowTitle } |
        Format-Table Id, ProcessName, Responding, MainWindowTitle -AutoSize | Out-Host
    try {
        Add-Type -AssemblyName System.Windows.Forms, System.Drawing
        $screen = [Windows.Forms.SystemInformation]::VirtualScreen
        $bitmap = [Drawing.Bitmap]::new($screen.Width, $screen.Height)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen($screen.Left, $screen.Top, 0, 0, $bitmap.Size)
        $bitmap.Save((Join-Path $out "$name-stuck.png"), [Drawing.Imaging.ImageFormat]::Png)
        $graphics.Dispose(); $bitmap.Dispose()
        Write-Host "--- screenshot in $(Join-Path $out "$name-stuck.png")"
    }
    catch { Write-Warning "No screenshot: $($_.Exception.Message)" }
}

function Installed { if (Test-Path $userKey) { Get-ItemProperty $userKey } }

function ExeVersion([string] $folder) {
    $exe = Join-Path $folder 'Stringline.exe'
    if (Test-Path $exe) { ((Get-Item $exe).VersionInfo.ProductVersion -split '\+')[0].Trim() }
}

function SetupVersion([string] $exe) { ((Get-Item $exe).VersionInfo.ProductVersion -split '\+')[0].Trim() }

# Uninstalls silently and waits for it to finish: the uninstaller hands over to a copy of itself in %TEMP% and returns
# at once, and that copy removes the user's data (when asked) after the program and its entry.
function Uninstall([string[]] $more = @(), [scriptblock] $done = { $true }) {
    $uninstaller = (Installed).UninstallString.Trim('"')
    $null = RunBounded $uninstaller (@('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') + $more) 'uninstall'
    $deadline = (Get-Date).AddSeconds(60)
    while (((Installed) -or (Test-Path (Join-Path $folder 'Stringline.exe')) -or -not (& $done)) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
}

# The settings as a hashtable, or null.
function SavedSettings {
    $file = Join-Path $roaming 'settings.json'
    if (Test-Path $file) { Get-Content -Raw $file | ConvertFrom-Json -AsHashtable }
}

# Copies the user's real settings, profiles and logs aside, for Restore-UserData to put back.
function Backup-UserData {
    if (Test-Path $backup) { Remove-Item -Recurse -Force $backup }
    New-Item -ItemType Directory -Force $backup | Out-Null
    foreach ($pair in @(@{ From = $roaming; To = 'roaming' }, @{ From = $local; To = 'local' })) {
        if (Test-Path $pair.From) { Copy-Item -Recurse $pair.From (Join-Path $backup $pair.To) }
    }
    $script:backedUp = $true
}

function Restore-UserData {
    if (-not $script:backedUp) { return }
    foreach ($pair in @(@{ To = $roaming; From = 'roaming' }, @{ To = $local; From = 'local' })) {
        if (Test-Path $pair.To) { Remove-Item -Recurse -Force $pair.To }
        $saved = Join-Path $backup $pair.From
        if (Test-Path $saved) { Copy-Item -Recurse $saved $pair.To }
    }
    Remove-Item -Recurse -Force $backup
    $script:backedUp = $false
    Write-Host 'Your settings, profiles and logs are back as they were.'
}

# The app's log lines since a time.
function AppLogSince([datetime] $since) {
    # By name, not by time written: Windows is slow to update that while the app has the file open.
    $days = $since.Date, (Get-Date).Date | Select-Object -Unique | ForEach-Object { "stringline-$($_.ToString('yyyyMMdd')).log" }
    Get-ChildItem $appLog -Filter 'stringline-*.log' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in $days } |
        ForEach-Object { Get-Content $_.FullName } |
        Where-Object { $_ -match '^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)' -and [datetime]$Matches[1] -ge $since.AddSeconds(-1) }
}

Push-Location $root
try {
    if ((Installed) -or (Test-Path $machineKey)) {
        if (-not $Force) { throw 'Stringline is already installed here. Uninstall it first, or pass -Force to replace it.' }
        Write-Warning 'Replacing the installed Stringline; it is uninstalled at the end.'
    }

    foreach ($pair in @(@{ Name = 'Old'; Version = '0.0.1' }, @{ Name = 'New'; Version = '0.0.2' })) {
        if (-not (Get-Variable $pair.Name -ValueOnly)) {
            Write-Host "Building the $($pair.Name.ToLower()) setup ($($pair.Version))..."
            & (Join-Path $PSScriptRoot 'publish.ps1') -Version $pair.Version -Installer -Output "$Output/$($pair.Version)" | Out-Host
            Set-Variable $pair.Name (Join-Path $out "$($pair.Version)\Stringline-$($pair.Version)-setup.exe")
        }
    }
    $Old = (Resolve-Path $Old).Path
    $New = (Resolve-Path $New).Path
    $oldVersion = SetupVersion $Old
    $newVersion = SetupVersion $New
    Write-Host "Testing an update from $oldVersion to $newVersion"

    Write-Host "Install $oldVersion"
    Check ((Setup $Old 'install-old') -eq 0) 'setup succeeds'
    $folder = (Installed).InstallLocation
    Check ((Installed).DisplayVersion -eq $oldVersion) "the Apps list shows $oldVersion"
    Check ((ExeVersion $folder) -eq $oldVersion) "Stringline.exe is $oldVersion"

    Write-Host "Start $oldVersion with a replay running"
    $started = Get-Date
    $wtt = Join-Path $root 'tests\fixtures\waterloo 2010.WTT'
    $replay = Join-Path $root 'tests\fixtures\waterloo-4x.sslog'
    $app = Start-Process (Join-Path $folder 'Stringline.exe') -PassThru `
        -ArgumentList '--wtt', "`"$wtt`"", '--replay', "`"$replay`"", '--speed', '8'
    $deadline = (Get-Date).AddSeconds(60)
    while (-not $app.HasExited -and (Get-Date) -lt $deadline -and -not (AppLogSince $started | Select-String 'Forecast of')) {
        Start-Sleep -Milliseconds 500
    }
    Check (-not $app.HasExited) 'the app is running'
    Check ([bool](AppLogSince $started | Select-String 'Forecast of')) 'the replay is running'

    Write-Host "Update to $newVersion while it runs"
    $updating = Get-Date
    Check ((Setup $New 'upgrade' @('/CLOSEAPPLICATIONS')) -eq 0) 'setup succeeds'
    Check ($app.WaitForExit(10000)) 'the running app was closed'
    $closing = AppLogSince $updating
    if ($closing | Select-String 'Windows is closing the app') {
        Check ([bool]($closing | Select-String 'Session stopped for Windows')) 'the app stopped and saved its session before closing'
    }
    else {
        # Before 0.1.1 the app did not stop its session when Windows asked it to close.
        Write-Warning "$oldVersion does not save its session when an installer closes it; later versions do."
    }
    Check ((Installed).DisplayVersion -eq $newVersion) "the Apps list shows $newVersion"
    Check ((Installed).InstallLocation -eq $folder) 'the install folder is unchanged'
    Check ((ExeVersion $folder) -eq $newVersion) "Stringline.exe is $newVersion"
    Check (-not (Test-Path $machineKey)) 'there is no all-users entry as well'

    if ($oldVersion -ne $newVersion) {
        Write-Host "Try to go back to $oldVersion"
        $code = Setup $Old 'downgrade-refused'
        if ($code -eq 0 -and -not (Select-String -Path (Join-Path $out 'downgrade-refused.log') -Pattern 'this setup is the older' -Quiet)) {
            # The check is in the older setup, and setups before 0.1.1 do not have it.
            Write-Warning "The $oldVersion setup does not check for a newer install; later setups do."
        }
        else {
            Check ($code -ne 0) 'a silent downgrade is refused'
            Check ((ExeVersion $folder) -eq $newVersion) "Stringline.exe is still $newVersion"
            Check ((Setup $Old 'downgrade' @('/ALLOWDOWNGRADE')) -eq 0) 'with /ALLOWDOWNGRADE it goes ahead'
        }
        Check ((ExeVersion $folder) -eq $oldVersion) "Stringline.exe is $oldVersion again"
        Check ((Setup $New 'upgrade-again') -eq 0) "and updates to $newVersion again"
    }

    Write-Host 'Uninstall silently, keeping the settings (S30)'
    Backup-UserData
    # Settings with a saved password, a profile and a log, to see which the uninstaller keeps.
    New-Item -ItemType Directory -Force (Join-Path $roaming 'profiles'), $appLog | Out-Null
    @{ host = 'upgrade-test'; port = 51515; userName = 'tester'; rememberPassword = $true; protectedPassword = 'bm90IGEgcmVhbCBwYXNzd29yZA==' } |
        ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $roaming 'settings.json')
    Set-Content (Join-Path $roaming 'profiles\upgrade-test.json') '{}'
    $documentsBefore = Test-Path $documents
    Uninstall
    Check (-not (Installed)) 'the uninstall entry is gone'
    Check (-not (Test-Path (Join-Path $folder 'Stringline.exe'))) 'Stringline.exe is gone'
    $kept = SavedSettings
    Check ($kept -and $kept.host -eq 'upgrade-test' -and $kept.userName -eq 'tester') 'the settings are kept'
    Check ($kept -and -not $kept.protectedPassword -and -not $kept.rememberPassword) 'the saved password is removed'
    Check (Test-Path (Join-Path $roaming 'profiles\upgrade-test.json')) 'the profiles are kept'
    Check (Test-Path $appLog) 'the logs are kept'

    Write-Host 'Install again, and uninstall with /REMOVEUSERDATA'
    Check ((Setup $New 'reinstall') -eq 0) 'setup succeeds'
    $folder = (Installed).InstallLocation
    Uninstall @('/REMOVEUSERDATA') { -not (Test-Path $roaming) -and -not (Test-Path $local) }
    Check (-not (Installed)) 'the uninstall entry is gone'
    Check (-not (Test-Path $roaming)) 'the settings and profiles are removed'
    Check (-not (Test-Path $local)) 'the logs and help pages are removed'
    Check (-not (Test-Path $unpacked)) 'the unpacked files are removed'
    Check ((Test-Path $documents) -eq $documentsBefore) 'Documents\Stringline is left alone'
}
finally {
    Restore-UserData
    Get-Process Stringline -ErrorAction SilentlyContinue | Where-Object { $folder -and $_.Path -like "$folder*" } | Stop-Process -Force
    Pop-Location
}

Write-Host "Setup logs are in $out"
if ($failures.Count) { throw "$($failures.Count) check(s) failed: $($failures -join '; ')" }
Write-Host 'The installer updates Stringline.' -ForegroundColor Green
