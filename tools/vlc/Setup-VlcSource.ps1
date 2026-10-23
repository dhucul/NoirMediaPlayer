<#
.SYNOPSIS
    Sets up NOIR's pinned local VLC source and fixes branch.
.DESCRIPTION
    Downloads source only. Does not change the playback runtime, reset a checkout,
    apply patches, or replace existing local commits/edits. -CheckOnly is offline.
#>
[CmdletBinding()]
param([switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
$taskRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$taskLock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'source.lock.json') -Raw | ConvertFrom-Json
if ($taskLock.schema -ne 1 -or $taskLock.revision -notmatch '^[a-fA-F0-9]{40}$' -or
    [string]::IsNullOrWhiteSpace($taskLock.repository) -or [string]::IsNullOrWhiteSpace($taskLock.branch)) {
    throw 'Invalid VLC source lock.'
}
$taskSource = [IO.Path]::GetFullPath((Join-Path $taskRepo $taskLock.sourceDirectory))
if (!$taskSource.StartsWith($taskRepo.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'VLC source must remain inside the NOIR repository.'
}
$taskGit = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
function Invoke-SourceGit([string[]]$GitArguments, [switch]$AllowFailure) {
    $taskPreviousPreference = $ErrorActionPreference
    try {
        # Windows PowerShell can promote native stderr to an error record. Git's
        # exit code determines success, including expected missing-ref queries.
        $ErrorActionPreference = 'Continue'
        $taskLines = @(& $taskGit @GitArguments 2>&1)
        $taskExit = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $taskPreviousPreference }
    if ($taskExit -ne 0) {
        if ($AllowFailure) { return }
        throw "Git failed ($taskExit): $($taskLines -join [Environment]::NewLine)"
    }
    $taskLines | ForEach-Object { $_.ToString() }
}
Invoke-SourceGit @('check-ref-format', '--branch', $taskLock.branch) | Out-Null
if (!(Test-Path -LiteralPath (Join-Path $taskSource '.git'))) {
    if ($CheckOnly) { throw 'VLC source is not set up; run this script without -CheckOnly.' }
    if ((Test-Path -LiteralPath $taskSource) -and @(Get-ChildItem -LiteralPath $taskSource -Force).Count) {
        throw 'The VLC source directory is not an empty directory or a standalone Git checkout. Existing files were preserved.'
    }
    New-Item -ItemType Directory -Path $taskSource -Force | Out-Null
    Invoke-SourceGit @('-c', "init.defaultBranch=$($taskLock.branch)", 'init', '--quiet', $taskSource) | Out-Null
    Invoke-SourceGit @('-C', $taskSource, 'config', '--local', 'core.autocrlf', 'false') | Out-Null
    Invoke-SourceGit @('-C', $taskSource, 'config', '--local', 'core.eol', 'lf') | Out-Null
    Invoke-SourceGit @('-C', $taskSource, 'config', '--local', 'core.longpaths', 'true') | Out-Null
    Invoke-SourceGit @('-C', $taskSource, 'remote', 'add', 'origin', $taskLock.repository) | Out-Null
}
$taskTop = (Invoke-SourceGit @('-C', $taskSource, 'rev-parse', '--show-toplevel')) -join ''
if ([IO.Path]::GetFullPath($taskTop) -ne $taskSource) { throw 'VLC source must have its own Git checkout.' }
$taskOrigin = (Invoke-SourceGit @('-C', $taskSource, 'remote', 'get-url', 'origin')) -join ''
if ($taskOrigin -ne $taskLock.repository) { throw 'The existing VLC origin does not match the lock. Existing checkout was preserved.' }
$taskObject = (Invoke-SourceGit @('-C', $taskSource, 'cat-file', '-t', $taskLock.revision) -AllowFailure) -join ''
if ($taskObject -ne 'commit') {
    if ($CheckOnly) { throw 'Pinned VLC commit is missing from the local checkout.' }
    Write-Host "Fetching VLC $($taskLock.release) at $($taskLock.revision)..."
    Invoke-SourceGit @('-C', $taskSource, 'fetch', '--quiet', '--no-tags', '--depth=1', 'origin', $taskLock.revision) | Out-Null
}
$taskHead = (Invoke-SourceGit @('-C', $taskSource, 'rev-parse', '--verify', 'HEAD') -AllowFailure) -join ''
if (!$taskHead) {
    if ($CheckOnly) { throw 'VLC source has not been checked out.' }
    Invoke-SourceGit @('-C', $taskSource, 'checkout', '--quiet', '-b', $taskLock.branch, $taskLock.revision) | Out-Null
    $taskHead = (Invoke-SourceGit @('-C', $taskSource, 'rev-parse', 'HEAD')) -join ''
}
$taskBranch = (Invoke-SourceGit @('-C', $taskSource, 'symbolic-ref', '--quiet', '--short', 'HEAD') -AllowFailure) -join ''
if ($taskBranch -ne $taskLock.branch) { throw "Expected VLC branch '$($taskLock.branch)', found '$taskBranch'. Existing checkout was preserved." }
Invoke-SourceGit @('-C', $taskSource, 'merge-base', '--is-ancestor', $taskLock.revision, 'HEAD') | Out-Null
foreach ($taskRelative in $taskLock.requiredFiles) {
    if (!(Test-Path -LiteralPath (Join-Path $taskSource $taskRelative) -PathType Leaf)) { throw "VLC source file is missing: $taskRelative" }
}
$taskBase = (Invoke-SourceGit @('-C', $taskSource, 'rev-parse', '--verify', 'refs/noir/upstream-base') -AllowFailure) -join ''
if ($taskBase -and $taskBase -ne $taskLock.revision) { throw 'Existing VLC baseline differs from the source lock. Rebase/review it explicitly; no reset was performed.' }
if (!$taskBase -and !$CheckOnly) { Invoke-SourceGit @('-C', $taskSource, 'update-ref', 'refs/noir/upstream-base', $taskLock.revision) | Out-Null }
$taskChanges = @(Invoke-SourceGit @('-C', $taskSource, 'status', '--porcelain'))
Write-Host "VLC source ready: $taskSource ($taskBranch). Playback binaries unchanged."
[pscustomobject]@{Source=$taskSource;Release=$taskLock.release;Baseline=$taskLock.revision;Head=$taskHead;Branch=$taskBranch;WorktreeChanges=$taskChanges.Count;OfflineCheck=[bool]$CheckOnly}
