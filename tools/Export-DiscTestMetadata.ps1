[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Destination
)

$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath($Source).TrimEnd('\') + '\'
$targetRoot = [IO.Path]::GetFullPath($Destination).TrimEnd('\') + '\'
if ($targetRoot.StartsWith($sourceRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $sourceRoot.StartsWith($targetRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Source and destination must be separate folders.'
}
if ([IO.Directory]::Exists($targetRoot) -or [IO.File]::Exists($Destination)) {
    throw 'Choose a new destination folder; existing files are never overwritten.'
}
try { $null = Get-Item -LiteralPath $sourceRoot -ErrorAction Stop }
catch { throw "Cannot read $sourceRoot. Run this helper in ordinary Windows PowerShell outside the restricted coding session. No administrator rights or permission changes should be necessary." }

$leaf = (Split-Path -Path $sourceRoot.TrimEnd('\') -Leaf)
$bdmv = if ($leaf -ieq 'BDMV') { $sourceRoot.TrimEnd('\') } else { Join-Path $sourceRoot 'BDMV' }
$videoTs = if ($leaf -ieq 'VIDEO_TS') { $sourceRoot.TrimEnd('\') } else { Join-Path $sourceRoot 'VIDEO_TS' }
$files = @()
$streams = @()
$format = ''
if ([IO.Directory]::Exists($bdmv)) {
    $format = 'Blu-ray'
    foreach ($name in @('index.bdmv', 'MovieObject.bdmv')) {
        $path = Join-Path $bdmv $name
        if ([IO.File]::Exists($path)) { $files += @{ Source=$path; Relative=('BDMV\' + $name) } }
    }
    foreach ($entry in @(@('PLAYLIST', '*.mpls'), @('CLIPINF', '*.clpi'))) {
        $folder = Join-Path $bdmv $entry[0]
        if (![IO.Directory]::Exists($folder)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $folder -Filter $entry[1] -File) {
            $files += @{ Source=$file.FullName; Relative=('BDMV\' + $entry[0] + '\' + $file.Name) }
        }
    }
    $streamFolder = Join-Path $bdmv 'STREAM'
    if ([IO.Directory]::Exists($streamFolder)) {
        $streams = @(Get-ChildItem -LiteralPath $streamFolder -Filter '*.m2ts' -File |
            Select-Object @{Name='Name';Expression={$_.Name}},Length)
    }
}
elseif ([IO.Directory]::Exists($videoTs)) {
    $format = 'DVD'
    foreach ($file in Get-ChildItem -LiteralPath $videoTs -File | Where-Object Extension -in '.ifo','.bup') {
        $files += @{ Source=$file.FullName; Relative=('VIDEO_TS\' + $file.Name) }
    }
}
else { throw 'The source has no BDMV or VIDEO_TS folder.' }
if ($files.Count -eq 0 -or $files.Count -gt 4096 -or $streams.Count -gt 4096) {
    throw 'The disc metadata is empty or exceeds the inspection file limit.'
}

# Validate the complete copy plan before creating any output. Encoded video/audio,
# AACS files and key databases are deliberately absent from this plan.
[long]$bytes = 0
foreach ($entry in $files) {
    $info = Get-Item -LiteralPath $entry.Source
    if (($info.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $info.Length -gt 4MB) {
        throw 'A metadata file is a link or exceeds the per-file limit.'
    }
    $bytes += $info.Length
    if ($bytes -gt 32MB) { throw 'Disc metadata exceeds the 32 MiB inspection limit.' }
}
[IO.Directory]::CreateDirectory($targetRoot) | Out-Null
$manifest = @()
foreach ($entry in $files) {
    $target = Join-Path $targetRoot $entry.Relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    [IO.File]::Copy($entry.Source, $target, $false)
    $manifest += [PSCustomObject]@{
        Path=$entry.Relative
        Length=(Get-Item -LiteralPath $target).Length
        SHA256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    }
}
[PSCustomObject]@{
    Format=$format
    Files=$manifest
    StreamInventory=$streams
    Note='Metadata only. No video, audio, AACS data or keys were copied. This export is not a playable disc.'
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $targetRoot 'disc-metadata.json') -Encoding UTF8
Write-Output "Copied $($files.Count) $format metadata files ($bytes bytes) to $targetRoot"
