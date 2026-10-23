# Shared by the native build commands. All environment changes are process-local.
$script:VlcRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$script:VlcToolchainLock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'toolchain.lock.json') -Raw | ConvertFrom-Json
if ($script:VlcToolchainLock.schema -ne 1 -or $script:VlcToolchainLock.environment -ne 'MINGW64' -or
    $script:VlcToolchainLock.target -ne 'x86_64-w64-mingw32') { throw 'Unsupported VLC toolchain lock.' }
$script:VlcToolchainRoot = [IO.Path]::GetFullPath((Join-Path $script:VlcRepo $script:VlcToolchainLock.directory))
if (!$script:VlcToolchainRoot.StartsWith($script:VlcRepo + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The private VLC toolchain must stay inside the NOIR repository.'
}
$script:VlcMsysRoot = Join-Path $script:VlcToolchainRoot 'msys64'
$script:VlcBash = Join-Path $script:VlcMsysRoot 'usr\bin\bash.exe'
$script:VlcCmakeRoot = [IO.Path]::GetFullPath((Join-Path $script:VlcToolchainRoot $script:VlcToolchainLock.cmake.directory))
if (!$script:VlcCmakeRoot.StartsWith($script:VlcToolchainRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The VLC CMake helper must stay in the private toolchain.'
}

function Get-VlcFileHash([string]$Path) {
    $taskStream = [IO.File]::OpenRead($Path)
    $taskSha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($taskSha.ComputeHash($taskStream))).Replace('-', '').ToLowerInvariant() }
    finally { $taskSha.Dispose(); $taskStream.Dispose() }
}

function Invoke-VlcShell {
    param([string]$Script, [string[]]$ShellArguments = @(), [string]$LogName, [switch]$AllowFailure, [switch]$Quiet)
    if (!(Test-Path -LiteralPath $script:VlcBash -PathType Leaf)) { throw 'Run Setup-VlcToolchain.ps1 first.' }
    $taskSaved = @{}
    foreach ($taskName in @('MSYSTEM','CHERE_INVOKING','MSYS2_PATH_TYPE','NOIR_VLC_REPO','NOIR_VLC_CMAKE')) {
        $taskSaved[$taskName] = [Environment]::GetEnvironmentVariable($taskName, 'Process')
    }
    $taskOldPreference = $ErrorActionPreference
    try {
        $env:MSYSTEM = 'MINGW64'
        $env:CHERE_INVOKING = '1'
        $env:MSYS2_PATH_TYPE = 'strict'
        $env:NOIR_VLC_REPO = $script:VlcRepo
        $env:NOIR_VLC_CMAKE = $script:VlcCmakeRoot
        $taskLogRoot = Join-Path $script:VlcToolchainRoot 'logs'
        New-Item -ItemType Directory -Path $taskLogRoot -Force | Out-Null
        $taskLog = Join-Path $taskLogRoot $LogName
        # Arguments are passed as arguments, never interpolated into shell code.
        $ErrorActionPreference = 'Continue'
        & $script:VlcBash --login $Script.Replace('\','/') @ShellArguments 2>&1 |
            Tee-Object -FilePath $taskLog | ForEach-Object { if (!$Quiet) { Write-Host $_.ToString() } }
        $taskExit = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $taskOldPreference
        foreach ($taskName in $taskSaved.Keys) { [Environment]::SetEnvironmentVariable($taskName, $taskSaved[$taskName], 'Process') }
    }
    if ($taskExit -ne 0 -and !$AllowFailure) { throw "VLC toolchain command failed ($taskExit). See $taskLog" }
    return $taskExit
}

function Enable-VlcToolAslr {
    # The MSYS runtime's upstream ASLR fix is also needed on helper DLLs when
    # Mandatory ASLR is enabled. Enable DYNAMIC_BASE only in this private tool
    # installation. Do not alter Windows policy or any playback runtime files.
    $taskChanged = 0
    foreach ($taskDll in Get-ChildItem -LiteralPath (Join-Path $script:VlcMsysRoot 'usr') -Filter '*.dll' -Recurse) {
        $taskBytes = [IO.File]::ReadAllBytes($taskDll.FullName)
        if ($taskBytes.Length -lt 64 -or [BitConverter]::ToUInt16($taskBytes, 0) -ne 0x5a4d) { continue }
        $taskPe = [BitConverter]::ToInt32($taskBytes, 60)
        if ($taskPe -lt 0 -or $taskPe + 96 -gt $taskBytes.Length -or [BitConverter]::ToUInt32($taskBytes, $taskPe) -ne 0x4550) { continue }
        $taskOffset = $taskPe + 94
        $taskFlags = [BitConverter]::ToUInt16($taskBytes, $taskOffset)
        if (($taskFlags -band 0x40) -ne 0) { continue }
        if ([BitConverter]::ToUInt16($taskBytes, $taskPe + 24) -ne 0x20b) { throw "Unexpected non-PE32+ tool DLL: $($taskDll.FullName)" }
        $taskFlagBytes = [BitConverter]::GetBytes([uint16]($taskFlags -bor 0x40))
        # Only the two-byte PE field changes; package payloads remain cached.
        $taskStream = [IO.File]::Open($taskDll.FullName, 'Open', 'Write', 'Read')
        try { $taskStream.Position = $taskOffset; $taskStream.Write($taskFlagBytes, 0, 2) }
        finally { $taskStream.Dispose() }
        $taskChanged++
    }
    Write-Host "Private MSYS helper DLLs prepared for ASLR ($taskChanged updated)."
}
