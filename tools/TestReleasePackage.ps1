[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Archive,
    [Parameter(Mandatory)]
    [string] $Manifest
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$metadata = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ne $metadata.Installer.Checksum) {
    throw 'Release archive checksum does not match manifest'
}
$expected = @(
    'PSF Guard Sync.dll', 'PSF Guard Sync.pdb',
    'PsfGuard.Nina.Sync.dll', 'PsfGuard.Nina.Sync.pdb',
    'System.Data.SQLite.dll', 'dll/x64/SQLite.Interop.dll',
    'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'PRIVACY.md'
)
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Archive).Path)
try {
    $entries = @($zip.Entries | Where-Object { $_.Name })
    $names = @($entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    if (@(Compare-Object ($expected | Sort-Object) ($names | Sort-Object)).Count -ne 0) {
        throw 'Release archive has missing, extra, or duplicate files'
    }
    $notices = @{
        'LICENSE' = Join-Path $root 'LICENSE'
        'THIRD-PARTY-NOTICES.txt' = Join-Path $root 'THIRD-PARTY-NOTICES.txt'
        'PRIVACY.md' = Join-Path $root 'docs/PRIVACY.md'
    }
    foreach ($name in $notices.Keys) {
        $entry = $entries | Where-Object { $_.FullName -eq $name }
        $stream = $entry.Open()
        try {
            $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))
            if ($hash -ne (Get-FileHash -LiteralPath $notices[$name]).Hash) {
                throw "Release notice differs from source: $name"
            }
        } finally {
            $stream.Dispose()
        }
    }
    $reader = [IO.StreamReader]::new($zip.GetEntry('THIRD-PARTY-NOTICES.txt').Open())
    try {
        $databaseNotices = $reader.ReadToEnd()
        foreach ($credit in @(
            'Tom Palmer (@tcpalmer)',
            'https://github.com/tcpalmer/nina.plugin.targetscheduler',
            'MPL-2.0',
            'System.Data.SQLite',
            'https://www.sqlite.org/copyright.html',
            'public domain',
            'MS-PL',
            'neither assembly is included'
        )) {
            if (!$databaseNotices.Contains($credit, [StringComparison]::Ordinal)) {
                throw "Missing database attribution in the release package: $credit"
            }
        }
    } finally {
        $reader.Dispose()
    }
} finally {
    $zip.Dispose()
}
Write-Output 'Verified archive contents, notice bytes, and manifest checksum.'
