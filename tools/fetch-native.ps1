<#
.SYNOPSIS
  Downloads the pinned third-party native binaries listed in native/manifest.json,
  verifies their SHA-256 and extracts the needed files into native/bin/<rid>/.
.EXAMPLE
  tools/fetch-native.ps1 -Rid win-x64,win-arm64
#>
[CmdletBinding()]
param(
    [string[]] $Rid = @('win-x64'),
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content (Join-Path $root 'native/manifest.json') -Raw | ConvertFrom-Json
$cache = Join-Path $root 'native/cache'
New-Item -ItemType Directory -Force $cache | Out-Null

# -Rid accepts both "a,b" (single string from cmd/bash) and a real array.
$rids = $Rid | ForEach-Object { $_ -split ',' } | Where-Object { $_ }

function Copy-Matching([string] $from, [string] $pattern, [string] $to) {
    $full = Join-Path $from $pattern
    $items = @(Get-Item -Path $full -ErrorAction SilentlyContinue)
    if ($items.Count -eq 0) { throw "Pattern '$pattern' matched nothing in $from" }
    New-Item -ItemType Directory -Force $to | Out-Null
    foreach ($i in $items) { Copy-Item $i.FullName -Destination $to -Force }
}

foreach ($r in $rids) {
    $binDir = Join-Path $root "native/bin/$r"
    New-Item -ItemType Directory -Force $binDir | Out-Null

    foreach ($b in $manifest.binaries) {
        $entry = $b.rids.$r
        if (-not $entry) { Write-Warning "$($b.name): no binary for $r, skipped"; continue }

        $stamp = Join-Path $binDir ".$($b.name).sha256"
        if (-not $Force -and (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $entry.sha256)) {
            Write-Host "$($b.name) $($b.version) [$r] up to date"
            continue
        }

        $archive = Join-Path $cache "$($b.name)-$r-$($entry.sha256.Substring(0,12)).$($b.archive)"
        if (-not (Test-Path $archive)) {
            Write-Host "$($b.name) $($b.version) [$r] downloading"
            Invoke-WebRequest -Uri $entry.url -OutFile "$archive.part" -UseBasicParsing
            Move-Item "$archive.part" $archive -Force
        }

        $actual = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $entry.sha256.ToLowerInvariant()) {
            Remove-Item $archive -Force
            throw "$($b.name) [$r]: SHA-256 mismatch. expected $($entry.sha256) got $actual"
        }

        $extract = Join-Path $cache "x-$($b.name)-$r"
        if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
        New-Item -ItemType Directory -Force $extract | Out-Null
        # Windows ships bsdtar, which reads both .tgz and .zip.
        & "$env:SystemRoot\System32\tar.exe" -xf $archive -C $extract
        if ($LASTEXITCODE -ne 0) { throw "$($b.name) [$r]: extraction failed" }

        foreach ($f in $b.files) { Copy-Matching $extract $f $binDir }
        if ($b.headers) { Copy-Matching $extract $b.headers (Join-Path $root "native/headers/$($b.name)") }
        foreach ($l in $b.licenseFiles) { Copy-Matching $extract $l (Join-Path $root "native/licenses/$($b.name)") }

        Remove-Item $extract -Recurse -Force
        Set-Content -Path $stamp -Value $entry.sha256 -NoNewline
        Write-Host "$($b.name) $($b.version) [$r] ready"
    }
}

Write-Host "Native binaries are in native/bin/<rid>/"
