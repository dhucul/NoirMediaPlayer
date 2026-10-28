# Building the NOIR installer

The 1.0.10 x64 installer fixes playback-core issues found in a logic and concurrency
audit. Windows-to-VLC handoffs no longer show a resume notice or seek to zero, a
native playback operation stalled for over 20 seconds is reported, commands are no
longer dropped silently while compatibility playback starts, and a startup watchdog
race is closed. Native video sizing from 1.0.9 is retained.

It packages the custom VLC engine selected by
[`DefaultRuntime.props`](../vlc/DefaultRuntime.props), DVD navigation, Blu-ray
support, AACS libraries, and the .NET 10 Windows desktop runtime. A separate .NET
installation is not needed.

Publish into a fresh directory and pass that exact directory to Inno Setup. The
installer checks the published application's version before compiling. Keep the
application project, `app.manifest`, and installer version in step for later releases.

From the main repository folder:

```powershell
$releaseOutput = Join-Path $PWD ('artifacts/publish/installer-' + (Get-Date -Format yyyyMMdd-HHmmss))
$buildOutput = Join-Path $PWD ('artifacts/installer-build/' + (Get-Date -Format yyyyMMdd-HHmmss) + '/')

dotnet publish src/NoirMediaPlayer -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:UseSharedCompilation=false -m:1 -nr:false "-p:BaseOutputPath=$buildOutput" -o $releaseOutput
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$innoCompiler = Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe'
& $innoCompiler "--define=PublishDir=$releaseOutput" ./tools/installer/NoirMediaPlayer.iss
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
```

This release uses the installed Inno Setup 7 compiler (engine version 7.1.0).
Adjust `$innoCompiler` if Inno Setup is installed elsewhere.

The result is `Installer/NOIR-Media-Player-Setup-1.0.10-x64.exe`, with an adjacent
`.sha256` checksum file. Setup retains the existing application identity and
shortcuts. During an upgrade it closes NOIR and replaces the entire bundled VLC
runtime directory, preventing stale modules or data from being mixed with the
new engine. User settings and the AACS key database remain outside that directory.

1.0.10 validation covers 111 logic tests, a 1193-file self-contained publish whose
application reports version 1.0.10.0, Setup version information, and Inno Setup
compilation without warnings. Installer payload extraction, the packaged snapshot
smoke test, and the UI playback suite were not run for this release. Validation
does not install Setup on the development machine or run a broad playback/disc or
GPU stress suite.

Historical 1.0.9 results remain in
`artifacts/installer-validation/1.0.9/verification.json`, and 1.0.8 results in
`artifacts/installer-validation/1.0.8/verification.json`. Historical 1.0.7 results
remain in `artifacts/vlc-validation/installer-1.0.7-verification.json`. They are
separate from the 1.0.10 test runs.
