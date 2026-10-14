<#
.SYNOPSIS
  End-to-end proof that an installed Modit updates itself: installs an old Setup.exe silently,
  starts the app against a feed that holds a newer version, waits until the app has restarted
  into that version, then uninstalls and checks that nothing was left behind.
.DESCRIPTION
  The feed is a folder (as produced by tools/build.ps1 under releases/<channel>/) or an http(s) URL.
  Run on a clean VM or CI runner for release gating; it also works on a developer machine because it
  installs into -InstallDir and removes everything again.
  Exit code 0 = update path works. Any failure throws.
.EXAMPLE
  tools/release/verify-update-path.ps1 -FromSetup artifacts/setup-0.1.0.exe -Feed artifacts/releases/win-x64-stable -ToVersion 0.1.1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $FromSetup,
    [Parameter(Mandatory)] [string] $Feed,
    [Parameter(Mandatory)] [string] $ToVersion,
    [string] $FromVersion,
    [string] $InstallDir = (Join-Path $env:TEMP "modit-verify-$([guid]::NewGuid().ToString('N').Substring(0,8))"),
    [int] $TimeoutSeconds = 180,
    [switch] $KeepInstalled
)

$ErrorActionPreference = 'Stop'
$FromSetup = (Resolve-Path $FromSetup).Path
if ($Feed -notmatch '^https?://') { $Feed = (Resolve-Path $Feed).Path }
$dataRoot = "$InstallDir-data"

function Get-InstalledVersion {
    $cli = Join-Path $InstallDir 'current\modit.exe'
    if (-not (Test-Path $cli)) { return $null }
    # "modit --version" prints SemVer build metadata ("0.3.0+<commit>"); compare the version part only.
    try { return (& $cli --version 2>$null | Select-Object -First 1).Trim().Split('+')[0] } catch { return $null }
}

function Test-OurProcess($process) {
    # Match on the unique folder name, not the full path: %TEMP% can be an 8.3 short path (C:\Users\RUNNER~1\...)
    # while the updater relaunches the app through the long path, so a prefix comparison misses it.
    $leaf = Split-Path $InstallDir -Leaf
    return $process.Path -and $process.Path.IndexOf("\$leaf\", [StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Stop-Modit {
    Get-Process -Name 'Modit.App', 'Modit.Engine.Host' -ErrorAction SilentlyContinue |
        Where-Object { Test-OurProcess $_ } |
        ForEach-Object { try { $_.Kill(); $_.WaitForExit(5000) | Out-Null } catch { } }
}

function Assert-True([bool] $condition, [string] $message) {
    if (-not $condition) { throw "FAILED: $message" }
    Write-Host "  ok  $message" -ForegroundColor Green
}

function Write-Diagnostics {
    # Shown only on failure: what the updater and the app logged, and any crash the OS recorded.
    $logs = @(Get-ChildItem $InstallDir, $dataRoot, (Join-Path $env:LOCALAPPDATA 'Velopack') -Recurse -Filter '*.log' -ErrorAction SilentlyContinue)
    foreach ($log in $logs) {
        Write-Host "---- $($log.FullName) (last 40 lines)" -ForegroundColor Yellow
        Get-Content $log.FullName -Tail 40 -ErrorAction SilentlyContinue | Write-Host
    }
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Level = 2; StartTime = (Get-Date).AddMinutes(-10) } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'Modit' } | Select-Object -First 5 |
        ForEach-Object { Write-Host "---- event $($_.Id) $($_.ProviderName)" -ForegroundColor Yellow; Write-Host $_.Message }
}

$installed = $false
try {
    Write-Host "1. Install $FromSetup -> $InstallDir" -ForegroundColor Cyan
    $setup = Start-Process -FilePath $FromSetup -ArgumentList '--silent', '--installto', "`"$InstallDir`"" -PassThru -Wait
    $installed = $true
    Assert-True ($setup.ExitCode -eq 0) "Setup exited with code 0 (was $($setup.ExitCode))"
    Stop-Modit   # Defensive: nothing of ours may be running before the test starts the app itself.

    $before = Get-InstalledVersion
    Assert-True ($null -ne $before) "Installed copy runs and reports a version ($before)"
    if ($FromVersion) { Assert-True ($before -eq $FromVersion) "Installed version is $FromVersion" }
    Assert-True ($before -ne $ToVersion) "Installed version differs from the target $ToVersion"
    Assert-True (Test-Path 'HKCU:\Software\Modit\Install') 'Install hook registered HKCU\Software\Modit\Install'
    Assert-True (Test-Path 'HKCU:\Software\Classes\Modit.Pdf\shell\open\command') 'Install hook registered the PDF "Open with" entry'

    Write-Host "2. Start the app against feed $Feed" -ForegroundColor Cyan
    $env:MODIT_UPDATE_FEED = $Feed
    $env:MODIT_UPDATE_CHECK_NOW = '1'
    $env:MODIT_UPDATE_AUTO_RESTART = '1'
    $env:MODIT_DATA_ROOT = $dataRoot

    # Seed a recognisable setting so we can prove user data is untouched by the update.
    New-Item -ItemType Directory -Force $dataRoot | Out-Null
    Set-Content -Path (Join-Path $dataRoot 'settings.json') -Value '{ "schemaVersion": 1, "theme": "Dark" }'
    Stop-Modit
    Start-Process -FilePath (Join-Path $InstallDir 'current\Modit.App.exe') | Out-Null

    Write-Host "3. Wait for the app to update itself to $ToVersion" -ForegroundColor Cyan
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $after = $null
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        $after = Get-InstalledVersion
        if ($after -eq $ToVersion) { break }
    }
    Assert-True ($after -eq $ToVersion) "Installed version is now $ToVersion (was $before, is $after)"

    # The updater must have relaunched the new version.
    $relaunched = $false
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and -not $relaunched) {
        Start-Sleep -Seconds 1
        $relaunched = @(Get-Process -Name 'Modit.App' -ErrorAction SilentlyContinue |
            Where-Object { Test-OurProcess $_ }).Count -gt 0
    }
    if (-not $relaunched) { Write-Diagnostics }
    Assert-True $relaunched 'The new version was started again after the update'
    $settings = Get-Content (Join-Path $dataRoot 'settings.json') -Raw | ConvertFrom-Json
    Assert-True ($settings.theme -eq 'Dark') 'User settings survived the update'
}
finally {
    foreach ($name in 'MODIT_UPDATE_FEED', 'MODIT_UPDATE_CHECK_NOW', 'MODIT_UPDATE_AUTO_RESTART', 'MODIT_DATA_ROOT') {
        Remove-Item "Env:$name" -ErrorAction SilentlyContinue
    }

    if ($installed -and -not $KeepInstalled) {
        Write-Host '4. Uninstall and check for residue' -ForegroundColor Cyan
        Stop-Modit
        $update = Join-Path $InstallDir 'Update.exe'
        if (Test-Path $update) {
            Start-Process -FilePath $update -ArgumentList 'uninstall', '--silent' -Wait | Out-Null
        }

        # Update.exe removes its own folder asynchronously after it exits.
        $deadline = (Get-Date).AddSeconds(30)
        while ((Test-Path $InstallDir) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
        Remove-Item $dataRoot -Recurse -Force -ErrorAction SilentlyContinue

        Assert-True (-not (Test-Path $InstallDir)) 'Install folder is gone'
        Assert-True (-not (Test-Path 'HKCU:\Software\Modit')) 'HKCU\Software\Modit is gone'
        Assert-True (-not (Test-Path 'HKCU:\Software\Classes\Modit.Pdf')) 'PDF "Open with" registration is gone'
        Assert-True (-not (Test-Path 'HKCU:\Software\Classes\Applications\Modit.App.exe')) 'Application registration is gone'
        Assert-True (-not (Test-Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Modit')) 'Uninstall entry is gone'
        $shortcuts = @(
            (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Modit.lnk'),
            (Join-Path ([Environment]::GetFolderPath('Programs')) 'Modit.lnk')
        ) | Where-Object { Test-Path $_ }
        Assert-True ($shortcuts.Count -eq 0) 'Desktop and Start menu shortcuts are gone'
    }
}

Write-Host 'Update path verified.' -ForegroundColor Green
