<#
.SYNOPSIS
  Builds a releasable Modit: self-contained app + engine host + CLI in one folder, then packages it.
.DESCRIPTION
  Output (under -OutDir, default artifacts/):
    publish/<rid>/                      the application folder (what gets installed)
    releases/<channel>/                 Velopack installer, full + delta packages, releases.<channel>.json
    Modit-portable-<rid>-<version>.zip  portable copy (portable.flag inside)
    checksums.txt                       SHA-256 of every distributable

  Delta packages are produced when releases/<channel>/ already holds the previous release
  (locally from an earlier run; in CI via "vpk download github" before this script).
.EXAMPLE
  tools/build.ps1 -Version 0.1.0 -Rid win-x64 -Channel stable
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [ValidateSet('win-x64', 'win-arm64')] [string] $Rid = 'win-x64',
    [ValidateSet('stable', 'beta')] [string] $Channel = 'stable',
    [ValidateSet('all', 'publish', 'velopack', 'portable')] [string] $Package = 'all',
    [string] $OutDir,
    [string] $ReleaseNotes,
    # Passed through to "vpk pack --azureTrustedSignFile" (CI). Without it the build is unsigned.
    [string] $AzureTrustedSignFile,
    [switch] $NoReadyToRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $root 'artifacts' }
$platform = if ($Rid -eq 'win-arm64') { 'ARM64' } else { 'x64' }
$publishDir = Join-Path $OutDir "publish/$Rid"
$velopackChannel = "$Rid-$Channel"      # e.g. win-x64-stable; must match UpdateRules.ChannelName
$releaseDir = Join-Path $OutDir "releases/$velopackChannel"

function Invoke-Checked([string] $what, [scriptblock] $block) {
    Write-Host "==> $what" -ForegroundColor Cyan
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$what failed with exit code $LASTEXITCODE" }
}

Push-Location $root
try {
    Invoke-Checked 'Fetch native engines' {
        powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'fetch-native.ps1') -Rid $Rid
    }

    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    $common = @(
        '-c', 'Release', '-r', $Rid, "-p:Platform=$platform", "-p:Version=$Version",
        '--self-contained', 'true', '-o', $publishDir, '-nologo', '-v', 'q',
        '-p:DebugType=none', '-p:GenerateDocumentationFile=false'
    )
    # Typed as an array: Windows PowerShell 5.1 would unwrap a one-element array and splat its characters.
    [string[]] $r2r = @()
    if (-not $NoReadyToRun) { $r2r += '-p:PublishReadyToRun=true' }

    # All three executables are self-contained and published into ONE folder so they share a single
    # copy of the .NET runtime instead of shipping it three times.
    Invoke-Checked 'Publish Modit.App' { dotnet publish src/Modit.App/Modit.App.csproj @common @r2r }
    Invoke-Checked 'Publish Modit.Engine.Host' { dotnet publish src/Modit.Engine.Host/Modit.Engine.Host.csproj @common @r2r }
    Invoke-Checked 'Publish modit CLI' { dotnet publish src/Modit.Cli/Modit.Cli.csproj @common @r2r }

    # License notices travel with the binaries (ADR-018). libvips ships its own list for the codecs it bundles.
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $publishDir
    $licenseDir = New-Item -ItemType Directory -Force (Join-Path $publishDir 'licenses')
    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE ".nuget/packages" }
    $vipsNotices = Get-ChildItem (Join-Path $nugetRoot "netvips.native.$Rid") -Recurse -Filter 'THIRD-PARTY-NOTICES.md' |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $vipsNotices) { throw "THIRD-PARTY-NOTICES.md of netvips.native.$Rid was not found in the NuGet cache" }
    Copy-Item $vipsNotices.FullName (Join-Path $licenseDir 'libvips-THIRD-PARTY-NOTICES.md')

    foreach ($required in 'Modit.App.exe', 'Modit.Engine.Host.exe', 'modit.exe', "native/$Rid/pdfium.dll", "native/$Rid/qpdf30.dll", 'libvips-42.dll', 'THIRD-PARTY-NOTICES.md') {
        if (-not (Test-Path (Join-Path $publishDir $required))) { throw "Publish output is missing $required" }
    }

    if ($Package -in 'all', 'velopack') {
        New-Item -ItemType Directory -Force $releaseDir | Out-Null
        $vpk = @(
            'vpk', 'pack',
            '--packId', 'Modit',
            '--packVersion', $Version,
            '--packDir', $publishDir,
            '--mainExe', 'Modit.App.exe',
            '--packTitle', 'Modit',
            '--packAuthors', 'Modit',
            '--icon', (Join-Path $root 'src/Modit.App/Assets/modit.ico'),
            '--channel', $velopackChannel,
            '--runtime', $Rid,
            '--shortcuts', 'Desktop,StartMenuRoot',
            '--outputDir', $releaseDir
        )
        if ($ReleaseNotes) { $vpk += @('--releaseNotes', $ReleaseNotes) }
        if ($AzureTrustedSignFile) { $vpk += @('--azureTrustedSignFile', $AzureTrustedSignFile) }
        else { Write-Warning 'No signing configuration: this is an UNSIGNED development build.' }
        Invoke-Checked 'Velopack pack' { dotnet @vpk }
    }

    if ($Package -in 'all', 'portable') {
        $zip = Join-Path $OutDir "Modit-portable-$Rid-$Version.zip"
        $stage = Join-Path $OutDir "portable-stage/$Rid"
        if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
        Copy-Item $publishDir $stage -Recurse
        # The flag switches all state to .\data and turns the updater off (docs/plan/14).
        Set-Content -Path (Join-Path $stage 'portable.flag') -Value 'Modit portable mode' -NoNewline
        if (Test-Path $zip) { Remove-Item $zip -Force }
        Write-Host '==> Portable zip' -ForegroundColor Cyan
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
        Remove-Item (Join-Path $OutDir 'portable-stage') -Recurse -Force
    }

    # Checksums of everything a user or administrator may download.
    $distributables = @()
    if (Test-Path $releaseDir) { $distributables += Get-ChildItem $releaseDir -File }
    $distributables += Get-ChildItem $OutDir -File -Filter '*.zip'
    $lines = $distributables | Sort-Object Name | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    }
    Set-Content -Path (Join-Path $OutDir 'checksums.txt') -Value $lines

    $size = (Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ('Done. App folder: {0:N0} MB in {1}' -f $size, $publishDir) -ForegroundColor Green
}
finally {
    Pop-Location
}
