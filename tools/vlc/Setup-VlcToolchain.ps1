<#
.SYNOPSIS
    Installs and verifies a private x64 MinGW/MSYS2 VLC build environment.
.DESCRIPTION
    No administrator access, global PATH changes, or playback runtime changes.
    -CheckOnly is offline. -Update performs a full MSYS2 package upgrade.
    Bootstrap archives are SHA256 pinned; signed packages and exact installed
    versions are retained under artifacts/vlc-toolchain for build provenance.
#>
[CmdletBinding()]
param([switch]$CheckOnly, [switch]$Update)
$ErrorActionPreference = 'Stop'
if ($CheckOnly -and $Update) { throw '-CheckOnly and -Update cannot be combined.' }
. (Join-Path $PSScriptRoot 'VlcToolchain.Common.ps1')
$taskMarker = Join-Path $script:VlcToolchainRoot 'setup-complete.json'
$taskOperationScript = Join-Path $PSScriptRoot 'toolchain.sh'
$taskLockHash = Get-VlcFileHash (Join-Path $PSScriptRoot 'toolchain.lock.json')
$taskSetupMatches = $false
if (Test-Path -LiteralPath $taskMarker) {
    $taskPreviousSetup = Get-Content -LiteralPath $taskMarker -Raw | ConvertFrom-Json
    $taskSetupMatches = $taskPreviousSetup.schema -eq 1 -and $taskPreviousSetup.lockSha256 -eq $taskLockHash
}

function Get-VlcBootstrapArchive($Archive) {
    if ($Archive.sha256 -notmatch '^[0-9a-f]{64}$' -or !([uri]$Archive.url).Scheme.Equals('https')) { throw 'Invalid bootstrap download lock.' }
    $taskDownloadRoot = Join-Path $script:VlcToolchainRoot 'downloads'
    New-Item -ItemType Directory -Path $taskDownloadRoot -Force | Out-Null
    $taskFile = Join-Path $taskDownloadRoot ([IO.Path]::GetFileName(([uri]$Archive.url).AbsolutePath))
    if (!(Test-Path -LiteralPath $taskFile)) {
        Write-Host "Downloading $($Archive.url)..."
        Invoke-WebRequest -UseBasicParsing -Uri $Archive.url -OutFile ($taskFile + '.partial')
        if ((Get-VlcFileHash ($taskFile + '.partial')) -ne $Archive.sha256) { throw 'Bootstrap archive SHA256 mismatch; nothing extracted.' }
        Move-Item -LiteralPath ($taskFile + '.partial') -Destination $taskFile
    }
    if ((Get-VlcFileHash $taskFile) -ne $Archive.sha256) { throw "Bootstrap archive SHA256 mismatch: $taskFile" }
    return $taskFile
}

if ($CheckOnly) {
    if (!$taskSetupMatches) { throw 'Toolchain setup does not match the lock; run without -CheckOnly.' }
}
elseif (!$taskSetupMatches -or $Update) {
    New-Item -ItemType Directory -Path $script:VlcToolchainRoot -Force | Out-Null
    $taskTar = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (!(Test-Path -LiteralPath $script:VlcBash)) {
        if (Test-Path -LiteralPath $script:VlcMsysRoot) { throw 'Incomplete existing MSYS2 directory preserved. Inspect it before retrying setup.' }
        $taskBase = Get-VlcBootstrapArchive $script:VlcToolchainLock.base
        & $taskTar -xf $taskBase -C $script:VlcToolchainRoot
        if ($LASTEXITCODE -ne 0) { throw 'MSYS2 base extraction failed.' }
    }
    $taskRuntimeDll = Join-Path $script:VlcMsysRoot 'usr\bin\msys-2.0.dll'
    $taskRuntimeText = (Get-Item -LiteralPath $taskRuntimeDll).VersionInfo.FileVersion
    if ($taskRuntimeText -notmatch '^(\d+\.\d+\.\d+)') { throw 'Cannot identify the private MSYS2 runtime.' }
    $taskRuntimeVersion = [version]$Matches[1]
    if ($taskRuntimeVersion -lt [version]$script:VlcToolchainLock.bootstrapRuntime.minimumVersion) {
        $taskRuntime = Get-VlcBootstrapArchive $script:VlcToolchainLock.bootstrapRuntime
        & $taskTar -xf $taskRuntime -C $script:VlcMsysRoot usr
        if ($LASTEXITCODE -ne 0) { throw 'MSYS2 runtime bootstrap failed.' }
    }
    Enable-VlcToolAslr
    Invoke-VlcShell $taskOperationScript @('keyring') 'keyring.log' | Out-Null
    # A core runtime update deliberately closes its MSYS shell. Start a fresh
    # shell, then finish the full upgrade; never use unsupported partial updates.
    for ($taskAttempt = 0; $taskAttempt -lt 3; $taskAttempt++) {
        $taskExit = Invoke-VlcShell $taskOperationScript @('update') "update-$taskAttempt.log" -AllowFailure
        Enable-VlcToolAslr
        if ($taskExit -eq 0) { break }
        $taskUpdateLog = Join-Path $script:VlcToolchainRoot "logs\update-$taskAttempt.log"
        if (!(Select-String -LiteralPath $taskUpdateLog -SimpleMatch 'To complete this update all MSYS2 processes' -Quiet)) {
            throw "MSYS2 upgrade failed. See $taskUpdateLog"
        }
    }
    if ($taskExit -ne 0) { throw 'MSYS2 core update did not finish after three fresh shells.' }
    Invoke-VlcShell $taskOperationScript (@('install') + @($script:VlcToolchainLock.packages)) 'install.log' | Out-Null
    Enable-VlcToolAslr
}

$taskCmakeExe = Join-Path $script:VlcCmakeRoot 'bin\cmake.exe'
if (!$CheckOnly) {
    $taskCmakeArchive = Get-VlcBootstrapArchive $script:VlcToolchainLock.cmake
    if (!(Test-Path -LiteralPath $taskCmakeExe)) {
        $taskHelpersRoot = Join-Path $script:VlcToolchainRoot 'helpers'
        New-Item -ItemType Directory -Path $taskHelpersRoot -Force | Out-Null
        & (Join-Path $env:SystemRoot 'System32\tar.exe') -xf $taskCmakeArchive -C $taskHelpersRoot
        if ($LASTEXITCODE -ne 0) { throw 'CMake helper extraction failed.' }
    }
}
if (!(Test-Path -LiteralPath $taskCmakeExe)) { throw 'Pinned CMake helper is missing. Run setup without -CheckOnly.' }
Invoke-VlcShell $taskOperationScript (@('verify') + @($script:VlcToolchainLock.packages)) 'verification.log' | Out-Null
if (!$CheckOnly) {
    [ordered]@{ schema=1; checkedUtc=[DateTime]::UtcNow.ToString('o'); target=$script:VlcToolchainLock.target;
        lockSha256=$taskLockHash; runtimeUnchanged=$true } |
        ConvertTo-Json | Set-Content -LiteralPath $taskMarker -Encoding utf8
}
Write-Host "VLC x64 toolchain verified: $script:VlcToolchainRoot"
Write-Host 'C, C++ and a Windows DLL compiled and ran successfully. NOIR playback binaries unchanged.'
