# Building the NOIR installer

The 1.0.7 x64 installer packages the custom VLC engine selected by
[`DefaultRuntime.props`](../vlc/DefaultRuntime.props), the owned-window resize
fix, DVD navigation, Blu-ray support and the retained AACS libraries. It includes
the .NET 10 Windows desktop runtime, so a separate .NET installation is not needed.

Publish into a fresh directory and pass that exact directory to Inno Setup. The
installer checks the published application's version before compiling. Keep the
application project, `app.manifest` and installer version in step for later releases.

From the main repository folder:

```powershell
$releaseOutput = Join-Path $PWD ('artifacts/publish/installer-' + (Get-Date -Format yyyyMMdd-HHmmss))
$buildOutput = Join-Path $PWD ('artifacts/installer-build/' + (Get-Date -Format yyyyMMdd-HHmmss) + '/')
dotnet publish src/NoirMediaPlayer -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:UseSharedCompilation=false -m:1 -nr:false "-p:BaseOutputPath=$buildOutput" -o $releaseOutput
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
& ./artifacts/installer-tools/innosetup-6.7.3/ISCC.exe "/DPublishDir=$releaseOutput" ./tools/installer/NoirMediaPlayer.iss
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
```

The private compiler was extracted from the signed official Inno Setup 6.7.3
archive; no compiler installer was run. Its origin, signature and hashes are
recorded in `artifacts/installer-tools/compiler.json`. Obtain a compiler from the
[official Inno Setup downloads](https://jrsoftware.org/isdl.php) on another machine.

The result is `Installer/NOIR-Media-Player-Setup-1.0.7-x64.exe`. Setup retains the
existing application identity and shortcuts. During an upgrade it closes NOIR and
replaces the entire bundled VLC runtime directory, preventing stale modules or
data from being mixed with the new engine. User settings and the AACS key database
remain outside that directory.

The release check extracts the completed installer without running Setup, compares
every extracted file with the publish output, and checks all 781 custom-engine
payload files against the DVD/Blu-ray-tested candidate. The 103 playback and host
regressions run against the exact published application DLL. Results are in
`artifacts/vlc-validation/installer-1.0.7-verification.json`; the repeated check is
`artifacts/vlc-validation/Verify-Installer107.ps1`.

The previous working player and all 375 official-runtime files remain in the
verified, runnable backup at
`artifacts/vlc-runtime-backups/videolan-3.0.23.1-before-custom-default-20261023-045930/player`.
The earlier `Installer/NOIR-Media-Player-Setup-1.0.6-x64.exe` is also retained.
