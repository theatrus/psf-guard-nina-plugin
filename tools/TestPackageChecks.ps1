#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $BuildOutput
)

$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "psf-guard-package-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $package = Join-Path $testRoot 'package'
    & "$PSScriptRoot/PreparePluginPackage.ps1" -BuildOutput $BuildOutput -PackageDirectory $package
    $validZip = Join-Path $testRoot 'valid.zip'
    Compress-Archive -Path "$package/*" -DestinationPath $validZip
    $manifest = Join-Path $testRoot 'manifest.json'

    function Write-TestManifest([string] $archive) {
        @{ Installer = @{ Checksum = (Get-FileHash -LiteralPath $archive).Hash } } |
            ConvertTo-Json | Set-Content -LiteralPath $manifest
    }
    function Assert-Rejected([scriptblock] $action, [string] $expected) {
        $failure = $null
        try { & $action | Out-Null } catch { $failure = $_.Exception.Message }
        if (!$failure -or !$failure.Contains($expected)) {
            throw "Expected rejection '$expected', received '$failure'"
        }
    }

    Write-TestManifest $validZip
    & "$PSScriptRoot/TestReleasePackage.ps1" -Archive $validZip -Manifest $manifest
    Assert-Rejected {
        & "$PSScriptRoot/PreparePluginPackage.ps1" -BuildOutput $BuildOutput -PackageDirectory $package
    } 'must be empty'
    Assert-Rejected {
        & "$PSScriptRoot/PreparePluginPackage.ps1" -BuildOutput $testRoot -PackageDirectory (Join-Path $testRoot 'missing')
    } 'Missing package source'

    # Mutate separate archives so each guard is exercised independently.
    foreach ($case in @('missing', 'missing-db-notice', 'extra', 'duplicate', 'changed-notice', 'changed-db-notice', 'checksum')) {
        $archive = Join-Path $testRoot "$case.zip"
        Copy-Item -LiteralPath $validZip -Destination $archive
        $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Update)
        try {
            switch ($case) {
                'missing' { $zip.GetEntry('LICENSE').Delete() }
                'missing-db-notice' { $zip.GetEntry('THIRD-PARTY-NOTICES.txt').Delete() }
                'extra' { $null = $zip.CreateEntry('unexpected.dll') }
                'duplicate' { $null = $zip.CreateEntry('LICENSE') }
                'changed-notice' {
                    $zip.GetEntry('PRIVACY.md').Delete()
                    $null = $zip.CreateEntry('PRIVACY.md')
                }
                'changed-db-notice' {
                    $zip.GetEntry('THIRD-PARTY-NOTICES.txt').Delete()
                    $null = $zip.CreateEntry('THIRD-PARTY-NOTICES.txt')
                }
                'checksum' { $null = $zip.CreateEntry('checksum-mismatch') }
            }
        } finally {
            $zip.Dispose()
        }
        if ($case -ne 'checksum') { Write-TestManifest $archive }
        $expected = switch ($case) {
            'changed-notice' { 'notice differs from source' }
            'changed-db-notice' { 'notice differs from source' }
            'checksum' { 'checksum does not match' }
            default { 'missing, extra, or duplicate' }
        }
        Assert-Rejected {
            & "$PSScriptRoot/TestReleasePackage.ps1" -Archive $archive -Manifest $manifest
        } $expected
    }
    Write-Output 'Package guards passed: valid archive, stale/missing inputs, missing/extra/duplicate entries, modified notices, and checksum mismatch.'
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $temp = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))
    if ([IO.Path]::GetDirectoryName($resolved) -ne $temp -or
        ![IO.Path]::GetFileName($resolved).StartsWith('psf-guard-package-')) {
        throw 'Refusing cleanup outside the temporary package-test directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
