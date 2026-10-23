<#
.SYNOPSIS
    Compares an explicitly selected VLC runtime with synthetic playback fixtures.
.DESCRIPTION
    Decodes to memory in a separate process, checks delivered video/PCM audio,
    pause, seek and accepted rate changes. Optional real-disc decoding uses the
    existing key database in place. Does not select a runtime for NOIR.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$RuntimeDirectory,
    [Parameter(Mandatory=$true)][string]$ResultsDirectory,
    [string]$DiscSource,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$taskRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$taskRuntime = (Resolve-Path -LiteralPath $RuntimeDirectory).Path
$taskResults = [IO.Path]::GetFullPath($ResultsDirectory)
$taskFixtures = Join-Path $taskRepo 'artifacts\vlc-validation\fixtures'
$taskProject = Join-Path $taskRepo 'tools\NoirMediaPlayer.VlcProbe\NoirMediaPlayer.VlcProbe.csproj'
$taskProbe = Join-Path (Split-Path $taskProject) 'bin\x64\Release\net10.0-windows\NoirMediaPlayer.VlcProbe.exe'
if (!$NoBuild) {
    dotnet build $taskProject -c Release -p:Platform=x64 -p:UseSharedCompilation=false -m:1 -nr:false --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Playback probe build failed.' }
}
& $taskProbe --generate $taskFixtures
if ($LASTEXITCODE -ne 0) { throw 'Synthetic fixture generation failed.' }
New-Item -ItemType Directory -Path $taskResults -Force | Out-Null
& $taskProbe --check-modules $taskRuntime (Join-Path $taskResults 'modules.json')
if ($LASTEXITCODE -ne 0) { throw 'A native plug-in could not load or expose the VLC 3 API. See modules.json.' }
& (Join-Path $taskRepo 'tools\Generate-PluginCache.ps1') -LibVlcDirectory $taskRuntime
$taskCases = @(
    @{Name='h264-aac.mp4';Video='true';Audio='true';Extra=@('--first-rgb','2032C8','--seek-rgb','C83220')},
    @{Name='aac.m4a';Video='false';Audio='true';Extra=@()},
    # Windows' fixture writer puts AAC in this program stream, which stock VLC
    # does not decode. Check the H.264 picture; TS/M2TS cover the A/V paths.
    @{Name='h264-program.mpg';Video='true';Audio='false';Extra=@()},
    @{Name='h264-transport.ts';Video='true';Audio='true';Extra=@()},
    @{Name='h264-transport.m2ts';Video='true';Audio='true';Extra=@()},
    @{Name='mjpeg-pcm.mkv';Video='true';Audio='true';Extra=@()},
    @{Name='wmv3-pcm.wmv';Video='true';Audio='true';Extra=@()}
)
if ($DiscSource) {
    $taskCases += @{Name='real-disc';Source=$DiscSource;Video='true';Audio='true';Extra=@('--controls','false','--timeout','60','--duration','15')}
}
$taskOutcomes = @()
foreach ($taskCase in $taskCases) {
    $taskMedia = if ($taskCase.Source) { $taskCase.Source } else { Join-Path $taskFixtures $taskCase.Name }
    $taskReport = Join-Path $taskResults ($taskCase.Name + '.json')
    $taskExtra = $taskCase.Extra
    & $taskProbe --runtime $taskRuntime --media $taskMedia --report $taskReport --video $taskCase.Video --audio $taskCase.Audio @taskExtra
    $taskExit = $LASTEXITCODE
    $taskData = if (Test-Path -LiteralPath $taskReport) { Get-Content -LiteralPath $taskReport -Raw | ConvertFrom-Json } else { $null }
    $taskOutcomes += [ordered]@{Name=$taskCase.Name;ExitCode=$taskExit;Report=$taskReport;Passed=($taskExit -eq 0 -and $taskData.Passed)}
}
$taskSummary = [ordered]@{
    Runtime=$taskRuntime;VerifiedUtc=[DateTime]::UtcNow.ToString('o');Cases=$taskOutcomes;
    Passed=@($taskOutcomes | Where-Object Passed).Count;Failed=@($taskOutcomes | Where-Object { !$_.Passed }).Count;
    Output='Memory video and PCM callbacks; physical display, speakers and disc menu interaction require application testing'
}
$taskSummary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskResults 'playback-summary.json') -Encoding utf8
if ($taskSummary.Failed) { throw "Playback checks failed: $($taskSummary.Failed). See $taskResults" }
Write-Host "Playback checks passed: $($taskSummary.Passed). Reports: $taskResults"
