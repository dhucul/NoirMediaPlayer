<#
.SYNOPSIS
    Stages an isolated NOIR runtime from a completed local VLC engine build.
.DESCRIPTION
    Uses only newly built VLC modules. Copies the retained AACS libraries and
    their notices, resolves MinGW DLL dependencies and records source provenance.
    Requires a new/empty destination; never changes NOIR's selected runtime.
#>
[CmdletBinding()]
param([string]$Destination)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'VlcToolchain.Common.ps1')
$taskBuild = Join-Path $script:VlcRepo 'artifacts\vlc-build'
$taskInstall = Join-Path $taskBuild 'install'
$taskSource = Join-Path $script:VlcRepo 'vendor\vlc'
$taskContrib = Join-Path $taskBuild 'x86_64-w64-mingw32'
$taskPluginSource = Join-Path $taskInstall 'lib\vlc\plugins'
$taskBuildLog = Join-Path $script:VlcToolchainRoot 'logs\build-Engine.log'
if (!(Test-Path -LiteralPath $taskBuildLog) -or
    !((Get-Content -LiteralPath $taskBuildLog -Tail 3) -contains 'Isolated VLC engine build completed; NOIR still uses the tested runtime.')) {
    throw 'The latest full Engine build has not completed successfully. Existing install files may be partial.'
}
foreach ($taskRequired in @('bin\libvlc.dll','bin\libvlccore.dll',
    'lib\vlc\plugins\access\libdvdnav_plugin.dll','lib\vlc\plugins\access\libdvdread_plugin.dll',
    'lib\vlc\plugins\access\liblibbluray_plugin.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $taskInstall $taskRequired))) { throw "Complete VLC build missing: $taskRequired" }
}
$taskJava = @(Get-ChildItem -LiteralPath (Join-Path $taskContrib 'share\java') -Filter '*.jar')
if (!($taskJava.Name -match '^libbluray-j2se-') -or !($taskJava.Name -match '^libbluray-awt-j2se-')) {
    throw 'Blu-ray Java menu JARs are missing. Complete the BD-J build before staging.'
}
$taskId = 'noir-vlc-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
if (!$Destination) { $Destination = Join-Path $script:VlcRepo "artifacts\vlc-candidates\$taskId" }
$Destination = [IO.Path]::GetFullPath($Destination)
if ((Test-Path -LiteralPath $Destination) -and @(Get-ChildItem -LiteralPath $Destination -Force).Count) {
    throw 'Use a new/empty candidate destination. Existing files were preserved.'
}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
function Copy-VlcTree([string]$From, [string]$To, [string[]]$SkipExtensions = @()) {
    if (!(Test-Path -LiteralPath $From -PathType Container)) { return }
    $taskFromRoot = [IO.Path]::GetFullPath($From).TrimEnd('\') + '\'
    foreach ($taskFile in Get-ChildItem -LiteralPath $From -File -Recurse) {
        if ($taskFile.Extension -in $SkipExtensions) { continue }
        $taskTarget = Join-Path $To $taskFile.FullName.Substring($taskFromRoot.Length)
        New-Item -ItemType Directory -Path (Split-Path -Parent $taskTarget) -Force | Out-Null
        Copy-Item -LiteralPath $taskFile.FullName -Destination $taskTarget
    }
}
Get-ChildItem -LiteralPath (Join-Path $taskInstall 'bin') -Filter '*.dll' | Copy-Item -Destination $Destination
Copy-VlcTree $taskPluginSource (Join-Path $Destination 'plugins') @('.a','.la')
# The installed cache predates stripping/copying; NOIR regenerates its own cache.
$taskOldCache = Join-Path $Destination 'plugins\plugins.dat'
if (Test-Path -LiteralPath $taskOldCache) { Remove-Item -LiteralPath $taskOldCache }
$taskJava | Copy-Item -Destination (Join-Path $Destination 'plugins\access')
Copy-VlcTree (Join-Path $taskInstall 'lib\vlc\lua') (Join-Path $Destination 'lua')
Copy-VlcTree (Join-Path $taskInstall 'share\vlc\lua') (Join-Path $Destination 'lua')
Copy-VlcTree (Join-Path $taskSource 'share\hrtfs') (Join-Path $Destination 'hrtfs')

# Keep unstripped originals in the build/install tree for debugging.
$taskStrip = Join-Path $script:VlcMsysRoot 'mingw64\bin\strip.exe'
foreach ($taskDll in Get-ChildItem -LiteralPath $Destination -Filter '*.dll' -Recurse) {
    & $taskStrip --strip-unneeded $taskDll.FullName
    if ($LASTEXITCODE -ne 0) { throw "Cannot strip candidate DLL: $($taskDll.FullName)" }
}
$taskAacs = Join-Path $script:VlcRepo 'src\NoirMediaPlayer\ThirdParty\Aacs'
foreach ($taskName in @('libaacs.dll','libgcrypt-20.dll','libgpg-error-0.dll')) {
    Copy-Item -LiteralPath (Join-Path $taskAacs $taskName) -Destination $Destination
}

# Find the complete import closure. Runtime DLLs are taken from this build's
# contrib/compiler installation, never from an unrelated installed VLC player.
$taskObjdump = Join-Path $script:VlcMsysRoot 'mingw64\bin\objdump.exe'
$taskSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$taskQueue = [Collections.Generic.Queue[string]]::new()
Get-ChildItem -LiteralPath $Destination -Filter '*.dll' -Recurse | ForEach-Object { $taskQueue.Enqueue($_.FullName) }
$taskImports = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
while ($taskQueue.Count) {
    $taskDll = $taskQueue.Dequeue()
    if (!$taskSeen.Add($taskDll)) { continue }
    $taskDump = @(& $taskObjdump -p $taskDll)
    if ($LASTEXITCODE -ne 0) { throw "Cannot inspect candidate DLL: $taskDll" }
    foreach ($taskLine in $taskDump) {
        if ($taskLine -notmatch '^\s*DLL Name:\s*(\S+)\s*$') { continue }
        $taskImport = $Matches[1]
        $taskImports.Add($taskImport) | Out-Null
        $taskExisting = Join-Path $Destination $taskImport
        if (Test-Path -LiteralPath $taskExisting) { $taskQueue.Enqueue($taskExisting); continue }
        if ($taskImport -match '^(api|ext)-ms-win-' -or
            (Test-Path -LiteralPath (Join-Path $env:SystemRoot "System32\$taskImport"))) { continue }
        $taskDependency = @((Join-Path $taskContrib "bin\$taskImport"),
            (Join-Path $script:VlcMsysRoot "mingw64\bin\$taskImport")) |
            Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (!$taskDependency) { throw "Unresolved import $taskImport in $taskDll" }
        Copy-Item -LiteralPath $taskDependency -Destination $taskExisting
        $taskQueue.Enqueue($taskExisting)
    }
}

$taskLicenseRoot = Join-Path $Destination 'licenses'
New-Item -ItemType Directory -Path $taskLicenseRoot -Force | Out-Null
foreach ($taskName in @('COPYING','COPYING.LIB','AUTHORS')) {
    Copy-Item -LiteralPath (Join-Path $taskSource $taskName) -Destination $taskLicenseRoot
}
Copy-VlcTree (Join-Path $taskAacs 'licenses') (Join-Path $taskLicenseRoot 'aacs')
Copy-Item -LiteralPath (Join-Path $taskAacs 'README.md') -Destination (Join-Path $taskLicenseRoot 'aacs\README.md')
Copy-VlcTree (Join-Path $taskContrib 'share\doc') (Join-Path $taskLicenseRoot 'contrib-doc')
Copy-VlcTree (Join-Path $script:VlcMsysRoot 'mingw64\share\licenses') (Join-Path $taskLicenseRoot 'mingw')
foreach ($taskComponent in Get-ChildItem -LiteralPath (Join-Path $taskBuild 'contrib') -Directory) {
    foreach ($taskNotice in Get-ChildItem -LiteralPath $taskComponent.FullName -File |
        Where-Object { $_.Name -match '^(COPYING|LICEN[CS]E|NOTICE)([.-].*)?$' }) {
        $taskNoticeDir = Join-Path $taskLicenseRoot ('contrib\' + $taskComponent.Name)
        New-Item -ItemType Directory -Path $taskNoticeDir -Force | Out-Null
        Copy-Item -LiteralPath $taskNotice.FullName -Destination $taskNoticeDir
    }
}

$taskRecordDir = Join-Path $taskBuild "source-records\$taskId"
New-Item -ItemType Directory -Path $taskRecordDir -Force | Out-Null
$taskUntracked = @(git -C $taskSource ls-files --others --exclude-standard)
if ($LASTEXITCODE -ne 0 -or $taskUntracked.Count) {
    throw 'Record new VLC source files with git add -N before staging, so the source patch includes them.'
}
$taskHead = (git -C $taskSource rev-parse HEAD) -join ''
if ($LASTEXITCODE -ne 0 -or $taskHead -notmatch '^[0-9a-f]{40}$') { throw 'Cannot record VLC source revision.' }
git -C $taskSource archive --format=tar "--output=$(Join-Path $taskRecordDir 'vlc-source.tar')" HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot archive the VLC source.' }
git -C $taskSource diff --binary --full-index --no-ext-diff --no-textconv "--output=$(Join-Path $taskRecordDir 'local.patch')" HEAD --
if ($LASTEXITCODE -ne 0) { throw 'Cannot record local source changes.' }
Copy-Item -LiteralPath (Join-Path $script:VlcToolchainRoot 'packages.txt') -Destination $taskRecordDir
Copy-Item -LiteralPath $taskBuildLog -Destination (Join-Path $taskRecordDir 'build-Engine.log')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'source.lock.json'),(Join-Path $PSScriptRoot 'toolchain.lock.json') -Destination $taskRecordDir
Copy-VlcTree $PSScriptRoot (Join-Path $taskRecordDir 'build-tools')
$taskSourceRecord = [ordered]@{
    schema=1; sourceRevision=$taskHead; baselineArchive='vlc-source.tar'; localChanges='local.patch';
    localChangesSha256=(Get-VlcFileHash (Join-Path $taskRecordDir 'local.patch'));
    dependencyTarballs=(Join-Path $taskSource 'contrib\tarballs');
    signedCompilerPackages=(Join-Path $script:VlcMsysRoot 'var\cache\pacman\pkg');
    importedDlls=@($taskImports | Sort-Object); releaseSourceBundleComplete=$false
}
$taskSourceRecord | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskRecordDir 'source-record.json') -Encoding utf8
$taskManifest = [ordered]@{
    schema=1; buildId=$taskId; sourceRepository='https://github.com/videolan/vlc.git';
    sourceRevision=$taskHead; correspondingSource=$taskRecordDir;
    patches=@('local.patch'); builtUtc=[DateTime]::UtcNow.ToString('o');
    purpose='Isolated development validation; current NOIR runtime remains selected';
    aacs='Retained tested bundled runtime'; sourceRecord=$taskSourceRecord
}
$taskManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Destination 'noir-vlc-build.json') -Encoding utf8
& (Join-Path $script:VlcRepo 'tools\Test-VlcRuntime.ps1') -RuntimeDirectory $Destination -BuildManifest (Join-Path $Destination 'noir-vlc-build.json')
Write-Host "Candidate staged: $Destination"
Write-Host 'Run decoding, application and real-disc checks before selecting this engine for a release.'
