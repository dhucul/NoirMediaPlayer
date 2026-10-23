[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Source,
    [string]$ReportDirectory = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'NoirMediaPlayer\NativeDiscTests')
)
$ErrorActionPreference = 'Stop'
if ([IntPtr]::Size -ne 8) { throw 'Run this test in 64-bit Windows PowerShell.' }
$fullSource = [IO.Path]::GetFullPath($Source)
$sourceLeaf = Split-Path -Path $fullSource.TrimEnd('\') -Leaf
$dvdSource = if ($sourceLeaf -ieq 'VIDEO_TS') { $fullSource.TrimEnd('\') } else { Join-Path $fullSource 'VIDEO_TS' }
if (![IO.File]::Exists((Join-Path $dvdSource 'VIDEO_TS.IFO'))) {
    if ([IO.Directory]::Exists((Join-Path $fullSource 'BDMV'))) {
        throw 'A Blu-ray is inserted. This separate test is for DVDs only. Insert a DVD or choose a VIDEO_TS folder.'
    }
    throw "No DVD VIDEO_TS.IFO is readable at $dvdSource. Insert a DVD or select a VIDEO_TS folder."
}
$project = Join-Path $PSScriptRoot 'NoirMediaPlayer.DvdTestHost\NoirMediaPlayer.DvdTestHost.csproj'
& dotnet build $project --configuration Release --verbosity minimal -p:Platform=x64 -p:UseSharedCompilation=false -p:RestoreIgnoreFailedSources=true -m:1 -nr:false
if ($LASTEXITCODE -ne 0) { throw 'The native DVD test build failed.' }
$target = Join-Path $PSScriptRoot 'NoirMediaPlayer.DvdTestHost\bin\x64\Release\net10.0-windows\NoirMediaPlayer.DvdTestHost.exe'
$reportFolder = [IO.Path]::GetFullPath($ReportDirectory)
[IO.Directory]::CreateDirectory($reportFolder) | Out-Null
$report = Join-Path $reportFolder ('dvd-native-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N') + '.jsonl')
Write-Output "Opening the SEPARATE window titled 'NOIR - Windows DVD playback test'. Report: $report"
Write-Output 'Check picture, sound, resize/aspect, pause/seek, chapters, menus, audio and subtitles. Close the test window when finished.'
# Both paths are file-system paths without embedded quotes or trailing backslashes.
if ($dvdSource.Contains('"') -or $report.Contains('"')) { throw 'Invalid test path.' }
$child = Start-Process -FilePath $target -ArgumentList ('--source "' + $dvdSource + '" --report "' + $report + '"') -WindowStyle Normal -PassThru -Wait
Write-Output "Native DVD test exited with code $($child.ExitCode). Report: $report"
if ($child.ExitCode -ne 0) { throw 'The native DVD test did not complete normally; retain compatibility playback and inspect its report.' }
