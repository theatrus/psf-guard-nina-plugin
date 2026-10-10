[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $BuildOutput,
    [Parameter(Mandatory)]
    [string] $PackageDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$files = [ordered]@{
    'PSF Guard Sync.dll' = Join-Path $BuildOutput 'PSF Guard Sync.dll'
    'PSF Guard Sync.pdb' = Join-Path $BuildOutput 'PSF Guard Sync.pdb'
    'PsfGuard.Nina.Sync.dll' = Join-Path $BuildOutput 'PsfGuard.Nina.Sync.dll'
    'PsfGuard.Nina.Sync.pdb' = Join-Path $BuildOutput 'PsfGuard.Nina.Sync.pdb'
    'System.Data.SQLite.dll' = Join-Path $BuildOutput 'System.Data.SQLite.dll'
    'dll/x64/SQLite.Interop.dll' = Join-Path $BuildOutput 'runtimes/win-x64/native/SQLite.Interop.dll'
    'LICENSE' = Join-Path $root 'LICENSE'
    'THIRD-PARTY-NOTICES.txt' = Join-Path $root 'THIRD-PARTY-NOTICES.txt'
    'PRIVACY.md' = Join-Path $root 'docs/PRIVACY.md'
}

# Refuse stale package contents instead of silently shipping extra dependencies.
if ((Test-Path -LiteralPath $PackageDirectory) -and
    @(Get-ChildItem -LiteralPath $PackageDirectory -Force).Count -gt 0) {
    throw "Package directory must be empty: $PackageDirectory"
}
foreach ($source in $files.Values) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Missing package source: $source"
    }
}
foreach ($entry in $files.GetEnumerator()) {
    $destination = Join-Path $PackageDirectory $entry.Key
    New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
    Copy-Item -LiteralPath $entry.Value -Destination $destination
    if ((Get-FileHash -LiteralPath $entry.Value).Hash -ne
        (Get-FileHash -LiteralPath $destination).Hash) {
        throw "Package copy differs from source: $($entry.Key)"
    }
}
Write-Output "Verified $($files.Count) runtime and notice files in $PackageDirectory"
