<#
.SYNOPSIS
    Checks the packaged x64 VLC 3 runtime and required disc components.
.DESCRIPTION
    Does not open a disc or change user settings. Native module loading and
    playback are checked separately by the regression suite and disc testing.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RuntimeDirectory,
    [string]$BuildManifest
)
$ErrorActionPreference = 'Stop'
$runtimeRoot = (Resolve-Path -LiteralPath $RuntimeDirectory).Path
$required = @('libvlc.dll', 'libvlccore.dll', 'plugins\access\libdvdnav_plugin.dll',
    'plugins\access\libdvdread_plugin.dll', 'plugins\access\liblibbluray_plugin.dll',
    'libaacs.dll', 'libgcrypt-20.dll', 'libgpg-error-0.dll')
$vlcVersion = $null
foreach ($relative in $required) {
    $path = Join-Path $runtimeRoot $relative
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required VLC/disc component is missing: $relative" }
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($path))
    try {
        if ($reader.BaseStream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5a4d) { throw "Invalid native component: $relative" }
        $reader.BaseStream.Position = 60
        $peOffset = $reader.ReadUInt32()
        if ($peOffset -lt 64 -or $peOffset -gt $reader.BaseStream.Length - 6) { throw "Invalid PE header: $relative" }
        $reader.BaseStream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw "VLC requires x64 components: $relative" }
    }
    finally { $reader.Dispose() }
    if ($relative -in $required[0..4]) {
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
        $parts = '{0}.{1}.{2}' -f $version.FileMajorPart, $version.FileMinorPart, $version.FileBuildPart
        if ($relative -eq 'libvlc.dll') {
            if ($version.FileMajorPart -ne 3) { throw 'This NOIR release requires the VLC 3 ABI; another major version needs a wrapper/API migration.' }
            $vlcVersion = $parts
        }
        elseif ($parts -ne $vlcVersion) { throw "VLC component version mismatch: $relative ($parts, expected $vlcVersion)" }
    }
}
if ($BuildManifest) {
    $build = Get-Content -LiteralPath $BuildManifest -Raw | ConvertFrom-Json
    if ($build.schema -ne 1 -or [string]::IsNullOrWhiteSpace($build.buildId) -or
        [string]::IsNullOrWhiteSpace($build.sourceRepository) -or $build.sourceRevision -notmatch '^[a-fA-F0-9]{40}$' -or
        [string]::IsNullOrWhiteSpace($build.correspondingSource)) {
        throw 'Custom VLC requires manifest schema 1, buildId, sourceRepository, exact 40-character sourceRevision and correspondingSource.'
    }
    Write-Host "Custom VLC build: $($build.buildId) ($($build.sourceRevision))"
}
Write-Host "VLC ${vlcVersion}: all 8 required x64 engine/DVD/Blu-ray/AACS components are present."
