<#
.SYNOPSIS
    Generates the LibVLC plugin cache (plugins.dat) for a NOIR output directory.

.DESCRIPTION
    Without plugins.dat, libvlc_new() recursively browses the plugin directory and
    LoadLibrary()s every one of the ~320 shipped plugins to read its descriptor. That
    costs ~80 ms of CPU on every launch and ~290 ms on a cold file cache, all of it
    before NOIR's window can appear, and it leaves ~100 MB of plugin images mapped
    into the process for the rest of the session.

    With the cache present libvlc reads the descriptors from one 260 KB file and only
    loads the plugins it actually needs: ~12 ms, and ~95 MB less mapped.

    The cache is produced by libvlc itself: passing --reset-plugins-cache makes it
    ignore any existing cache, rebuild the module bank and write plugins.dat next to
    the plugins. This is the same mechanism VideoLAN's vlc-cache-gen.exe uses, so no
    extra tool has to be downloaded or vendored.

    Cache entries record each plugin's path relative to the plugin directory plus its
    size and last-write time, so the file stays valid when the build output is copied
    into the installer and installed elsewhere - provided the copy preserves
    timestamps, which both MSBuild and Inno Setup do by default. A cache that does go
    stale is rejected by libvlc and it falls back to browsing, so a bad cache can only
    cost startup time, never correctness.

.PARAMETER LibVlcDirectory
    The libvlc runtime directory holding libvlc.dll, libvlccore.dll and plugins\,
    e.g. artifacts\publish\win-x64\libvlc\win-x64.

.PARAMETER Force
    Rebuild even when plugins.dat is already newer than every plugin.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$LibVlcDirectory,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $LibVlcDirectory)) {
    Write-Host "Plugin cache skipped: '$LibVlcDirectory' does not exist."
    exit 0
}

$libVlcDir = (Resolve-Path -LiteralPath $LibVlcDirectory).Path
$pluginsDir = Join-Path $libVlcDir 'plugins'
$core = Join-Path $libVlcDir 'libvlccore.dll'
$libvlc = Join-Path $libVlcDir 'libvlc.dll'
$cache = Join-Path $pluginsDir 'plugins.dat'

foreach ($required in @($pluginsDir, $core, $libvlc)) {
    if (-not (Test-Path -LiteralPath $required)) {
        Write-Host "Plugin cache skipped: '$required' not found."
        exit 0
    }
}

$plugins = @(Get-ChildItem -LiteralPath $pluginsDir -Recurse -File -Filter *.dll)
if ($plugins.Count -eq 0) {
    Write-Host "Plugin cache skipped: no plugins under '$pluginsDir'."
    exit 0
}

# The cache records every plugin's size and timestamp, so it only has to be rebuilt when a
# plugin is newer than the cache. Subdirectory timestamps are compared too: a plugin that was
# excluded from the build is deleted rather than modified, which leaves every surviving file
# older than the cache but does bump the directory holding it. The plugins root itself is left
# out of that comparison because writing plugins.dat into it bumps the root every time, which
# would make the cache look permanently stale.
if (-not $Force -and (Test-Path -LiteralPath $cache)) {
    $cacheTime = (Get-Item -LiteralPath $cache).LastWriteTimeUtc
    $directories = @(Get-ChildItem -LiteralPath $pluginsDir -Recurse -Directory)
    $newest = (@($plugins) + $directories | Measure-Object -Property LastWriteTimeUtc -Maximum).Maximum
    if ($cacheTime -ge $newest) {
        Write-Host ("Plugin cache up to date ({0:N0} plugins)." -f $plugins.Count)
        exit 0
    }
}

$source = @'
using System;
using System.Runtime.InteropServices;

public static class VlcCacheGen
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public delegate IntPtr LibVlcNew(int argc, string[] argv);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void LibVlcRelease(IntPtr instance);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW(string path);

    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    public static string Generate(string corePath, string libVlcPath, string libVlcDirectory)
    {
        // libvlc derives the plugin directory from libvlccore's own module path, so the
        // runtime under test is selected purely by which files are loaded here.
        SetDllDirectoryW(libVlcDirectory);
        if (LoadLibraryW(corePath) == IntPtr.Zero)
        {
            return "LoadLibrary failed for " + corePath + " (error " + Marshal.GetLastWin32Error() + ")";
        }

        IntPtr module = LoadLibraryW(libVlcPath);
        if (module == IntPtr.Zero)
        {
            return "LoadLibrary failed for " + libVlcPath + " (error " + Marshal.GetLastWin32Error() + ")";
        }

        IntPtr newPtr = GetProcAddress(module, "libvlc_new");
        IntPtr releasePtr = GetProcAddress(module, "libvlc_release");
        if (newPtr == IntPtr.Zero || releasePtr == IntPtr.Zero)
        {
            return "libvlc_new/libvlc_release not exported by " + libVlcPath;
        }

        var create = (LibVlcNew)Marshal.GetDelegateForFunctionPointer(newPtr, typeof(LibVlcNew));
        var release = (LibVlcRelease)Marshal.GetDelegateForFunctionPointer(releasePtr, typeof(LibVlcRelease));

        // --reset-plugins-cache: ignore any existing plugins.dat, rebuild the module
        // bank from disk and write the cache back out.
        // --ignore-config: never read or write the user's VLC configuration.
        string[] args = { "--quiet", "--ignore-config", "--reset-plugins-cache" };
        IntPtr instance = create(args.Length, args);
        if (instance == IntPtr.Zero)
        {
            return "libvlc_new returned NULL";
        }

        release(instance);
        return null;
    }
}
'@

Add-Type -TypeDefinition $source -Language CSharp | Out-Null

# The existing cache is deliberately left in place: --reset-plugins-cache overwrites it, and
# deleting it up front would throw away a working cache if generation then failed. Its
# timestamp is kept instead, so a run that silently writes nothing is still caught below.
$previousWrite = if (Test-Path -LiteralPath $cache) { (Get-Item -LiteralPath $cache).LastWriteTimeUtc } else { $null }

$failure = [VlcCacheGen]::Generate($core, $libvlc, $libVlcDir)
if ($failure) {
    Write-Error "Plugin cache generation failed: $failure"
    exit 1
}

if (-not (Test-Path -LiteralPath $cache)) {
    Write-Error "Plugin cache generation failed: '$cache' was not written."
    exit 1
}

if ($null -ne $previousWrite -and (Get-Item -LiteralPath $cache).LastWriteTimeUtc -le $previousWrite) {
    Write-Error "Plugin cache generation failed: '$cache' was not rewritten."
    exit 1
}

$cacheItem = Get-Item -LiteralPath $cache
Write-Host ("Plugin cache written: {0} ({1:N0} bytes, {2:N0} plugins)." -f $cache, $cacheItem.Length, $plugins.Count)
exit 0
