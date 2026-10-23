<#
.SYNOPSIS
    Builds the local VLC source with the private NOIR x64 toolchain.
.DESCRIPTION
    Bootstrap generates VLC's autotools and contrib makefiles. DvdLibraries also
    compiles the pinned DVD dependencies. Engine builds all engine dependencies,
    VLC DLLs/plugins and installs them into artifacts/vlc-build/install.
    Nothing here changes the NOIR project's selected playback runtime.
#>
[CmdletBinding()]
param(
    [ValidateSet('Bootstrap','DvdLibraries','Engine','RebuildEngine')][string]$Stage = 'Engine',
    [ValidateRange(1,32)][int]$Jobs = 4,
    [string]$JavaHome
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'VlcToolchain.Common.ps1')
& (Join-Path $PSScriptRoot 'Setup-VlcSource.ps1') -CheckOnly | Out-Null
& (Join-Path $PSScriptRoot 'Setup-VlcToolchain.ps1') -CheckOnly
$taskPreviousJava = $env:JAVA_HOME
try {
    if ($Stage -eq 'Engine') {
        if (!$JavaHome) { $JavaHome = $env:JAVA_HOME }
        if (!$JavaHome -and (Test-Path -LiteralPath 'HKLM:\SOFTWARE\JavaSoft\JDK')) {
            $taskJdkVersion = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\JavaSoft\JDK').CurrentVersion
            $JavaHome = (Get-ItemProperty -LiteralPath "HKLM:\SOFTWARE\JavaSoft\JDK\$taskJdkVersion").JavaHome
        }
        if (!$JavaHome -or !(Test-Path -LiteralPath (Join-Path $JavaHome 'include\jni.h')) -or
            !(Test-Path -LiteralPath (Join-Path $JavaHome 'bin\javac.exe'))) {
            throw 'Blu-ray Java support requires a JDK. Supply -JavaHome or configure JAVA_HOME for this build.'
        }
        $env:JAVA_HOME = [IO.Path]::GetFullPath($JavaHome)
    }
    $taskLogStage = if ($Stage -eq 'RebuildEngine') { 'Engine' } else { $Stage }
    Write-Host "VLC $Stage log: $(Join-Path $script:VlcToolchainRoot "logs\build-$taskLogStage.log")"
    Invoke-VlcShell (Join-Path $PSScriptRoot 'build-vlc.sh') @($Stage, "$Jobs") "build-$taskLogStage.log" -Quiet:($taskLogStage -eq 'Engine') | Out-Null
}
finally { $env:JAVA_HOME = $taskPreviousJava }
Write-Host "VLC $Stage stage completed. Output: $(Join-Path $script:VlcRepo 'artifacts\vlc-build')"
if ($Stage -in @('Engine','RebuildEngine')) {
    Write-Host 'This is an isolated development build. Validate and package the complete runtime before using it in NOIR.'
}
