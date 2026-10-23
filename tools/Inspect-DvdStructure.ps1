[CmdletBinding()]
param(
    [string]$Source = 'G:\',
    [string]$ReportDirectory = (Join-Path $env:LOCALAPPDATA 'NoirMediaPlayer\NativeDiscTests')
)

$ErrorActionPreference = 'Stop'
$fullSource = [IO.Path]::GetFullPath($Source)
if ($fullSource.StartsWith('\\', [StringComparison]::Ordinal)) { throw 'Choose a local DVD or metadata folder.' }
if ([IO.Path]::GetFileName($fullSource) -ieq 'VIDEO_TS.IFO') { $fullSource = [IO.Path]::GetDirectoryName($fullSource) }
$videoTs = if ([IO.Path]::GetFileName($fullSource.TrimEnd('\')) -ieq 'VIDEO_TS') {
    $fullSource
} else { Join-Path $fullSource 'VIDEO_TS' }
$videoTs = $videoTs.TrimEnd('\')
if (![IO.Directory]::Exists($videoTs)) { throw "No readable VIDEO_TS folder was found at $videoTs. This check needs a DVD or a DVD metadata export." }

$project = Join-Path $PSScriptRoot 'NoirMediaPlayer.DiscInspector\NoirMediaPlayer.DiscInspector.csproj'
& dotnet build $project --configuration Release --verbosity minimal -p:Platform=x64 -p:UseSharedCompilation=false -p:RestoreIgnoreFailedSources=true -m:1 -nr:false
if ($LASTEXITCODE -ne 0) { throw 'The first-party disc inspector build failed.' }
$program = Join-Path $PSScriptRoot 'NoirMediaPlayer.DiscInspector\bin\x64\Release\net10.0-windows\NoirMediaPlayer.DiscInspector.exe'
$folder = [IO.Path]::GetFullPath($ReportDirectory)
[IO.Directory]::CreateDirectory($folder) | Out-Null
$report = Join-Path $folder ('dvd-structure-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N') + '.json')
Write-Output 'Reading DVD information files using NOIR code. No video or decryption keys are read; playback settings are unchanged.'
& $program --source $videoTs --output $report
if ($LASTEXITCODE -ne 0) { throw "DVD inspection reported an error. If a report was created, it is at $report" }
Write-Output "DVD structure report saved: $report"
