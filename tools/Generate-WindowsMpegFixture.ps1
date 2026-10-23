param(
    [Parameter(Mandatory = $true)][string] $Source,
    [Parameter(Mandatory = $true)][string] $Destination,
    [switch] $VideoOnly,
    [ValidateSet(240, 480)][int] $Height = 240
)

# Test media only: encode our synthetic MP4 through Windows' MPEG program-stream
# sink. No external media, encoder executable or third-party codec is used.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime

function Await-Operation($Operation, [Type] $ResultType) {
    $method = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetGenericArguments().Count -eq 1 -and
        $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    } | Select-Object -First 1
    $task = $method.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
    $task.GetAwaiter().GetResult()
}

$sourceFile = [IO.File]::OpenRead($Source)
try {
    $destinationFile = [IO.File]::Create($Destination)
    try {
        $inputStream = [System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($sourceFile)
        $outputStream = [System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($destinationFile)
        $profile = [Windows.Media.MediaProperties.MediaEncodingProfile,Windows.Media.MediaProperties,ContentType=WindowsRuntime]::CreateMp4(
            [Windows.Media.MediaProperties.VideoEncodingQuality,Windows.Media.MediaProperties,ContentType=WindowsRuntime]::Wvga)
        $profile.Container.Subtype = 'MPEG2'
        $profile.Video.Width = 320
        $profile.Video.Height = $Height
        $profile.Video.Bitrate = 250000
        if ($VideoOnly) { $profile.Audio = $null }
        $transcoder = [Windows.Media.Transcoding.MediaTranscoder,Windows.Media.Transcoding,ContentType=WindowsRuntime]::new()
        $transcoder.HardwareAccelerationEnabled = $false
        $prepared = Await-Operation ($transcoder.PrepareStreamTranscodeAsync($inputStream, $outputStream, $profile)) (
            [Windows.Media.Transcoding.PrepareTranscodeResult,Windows.Media.Transcoding,ContentType=WindowsRuntime])
        if (-not $prepared.CanTranscode) { throw "Windows MPEG fixture encoding failed: $($prepared.FailureReason)" }
        $method = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
            $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetGenericArguments().Count -eq 1 -and
            $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncActionWithProgress`1'
        } | Select-Object -First 1
        $task = $method.MakeGenericMethod([double]).Invoke($null, @($prepared.TranscodeAsync()))
        $null = $task.GetAwaiter().GetResult()
        $null = Await-Operation ($outputStream.FlushAsync()) ([bool])
    }
    finally { $destinationFile.Dispose() }
}
finally { $sourceFile.Dispose() }
