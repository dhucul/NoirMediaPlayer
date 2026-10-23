# Maintaining NOIR's VLC engine

NOIR keeps VLC for DVD/Blu-ray and for ordinary-media cases that need it. NOIR's
C#/WPF code can be changed directly. Engine bugs can also be fixed in a local VLC
source branch and shipped in NOIR, without waiting for an upstream binary release.
VLC engine changes use its native source/build system; they are separate from the
.NET build. The native Windows disc replacement is no longer an active migration.

## Standard releases

`VlcRuntimeVersion` in `src/NoirMediaPlayer/NoirMediaPlayer.csproj` selects both
official VideoLAN NuGet packages. Change that property once for an engine upgrade.
The base package supplies the runtime; only DVD navigation/reading modules are
taken from its matching GPL package. LibVLCSharp is versioned separately and must
support the selected VLC ABI. The current supported engine family is VLC 3.

Restore, build and test before publishing to an empty directory. Both build and
publish check the architecture and versions of the core/disc modules, check AACS
dependencies and regenerate the module cache when binaries change. The installer
replaces the plug-in directory, including removed upstream modules. Settings and
the user's AACS key database remain outside that directory.

## Fixing the engine ourselves

The source is set up inside the main NOIR folder at `vendor/vlc`, on its own local
`noir/fixes` branch. The current 3.0.23 baseline is pinned in `source.lock.json` to
`79128878ddb2c280bbb6c89c76a46b31a80ade1c`, matching the changeset in the tested
Windows runtime (including its H.26x demux error-handling fix). The `3.0.23.1` NuGet package pin is a
separate packaging version. From the NOIR repository root, recreate or check the
checkout with:

```powershell
& ./tools/vlc/Setup-VlcSource.ps1
& ./tools/vlc/Setup-VlcSource.ps1 -CheckOnly
```

The [upstream Windows build script](https://github.com/videolan/vlc/blob/3.0.23/extras/package/win32/build.sh)
supports x86_64 and an engine-only build. Use the matching release's configured
MinGW cross-build environment, retain DVD/Blu-ray support and stage the resulting
Windows runtime as a complete set. Do not mix individual plug-ins from a different
build. The repository's master branch targets a different development baseline;
use the supported release branch for a fix.

Setup fetches the exact pinned commit, uses LF source files and checks the origin,
branch and baseline ancestry on later runs. It preserves existing local commits
and edits; it never resets, pulls new upstream code or applies patches. The
checkout is ignored by the parent NOIR repository so upstream files do not flood
NOIR's changes. The lock, setup script and [our patches](../../patches/vlc/README.md)
are tracked in NOIR. Preserve our source changes there before moving the project.
The local `refs/noir/upstream-base` ref identifies the locked patch base.

On a fresh pinned checkout, apply the recorded Windows build fixes explicitly
before building (the existing development checkout already has these changes):

```powershell
git -C vendor/vlc apply --check ../../patches/vlc/0001-native-build-compat.patch
git -C vendor/vlc apply ../../patches/vlc/0001-native-build-compat.patch
git -C vendor/vlc apply --check ../../patches/vlc/0002-snapshot-without-stream-output.patch
git -C vendor/vlc apply ../../patches/vlc/0002-snapshot-without-stream-output.patch
```

Useful starting points in `vendor/vlc`:

| Area | Source |
| --- | --- |
| DVD menus and stream events | `modules/access/dvdnav.c` |
| DVD title reading | `modules/access/dvdread.c` |
| Blu-ray menus/playlists and overlays | `modules/access/bluray.c` |
| Decoder integration | `modules/codec/avcodec/` |
| Input scheduling and playback state | `src/input/` |
| Public libVLC API | `lib/` and `include/vlc/` |
| Windows build | `extras/package/win32/build.sh` |
| Disc dependency versions/patches | `contrib/src/dvdnav/`, `dvdread/`, `dvdcss/`, `bluray/` |

Keep a NOIR regression test with each engine fix. The dependency implementations
are separate projects; VLC's contrib recipes fetch their pinned sources during
the native build. For separate libaacs source/provenance, see
`src/NoirMediaPlayer/ThirdParty/Aacs/README.md`. A fix in one of these libraries
needs its own source/patch record as well.

## Native Windows build tools

The private MSYS2/MinGW x64 toolchain lives at `artifacts/vlc-toolchain/msys64`.
It runs on Windows without WSL, administrator access or global PATH changes.
From the NOIR repository root:

```powershell
& ./tools/vlc/Setup-VlcToolchain.ps1
& ./tools/vlc/Setup-VlcToolchain.ps1 -CheckOnly
```

Setup verifies SHA256-pinned official bootstrap archives, initializes package
signatures and installs the packages listed in `toolchain.lock.json`. Subsequent
runs verify the installed environment; `-Update` explicitly performs a full
MSYS2 upgrade. MSYS2 packages follow its rolling repositories: `packages.txt`,
downloaded package archives and logs under `artifacts/vlc-toolchain` record the
exact versions used. The bootstrap lock alone does not make later builds
byte-for-byte reproducible. Preserve that directory's package inventory/cache
with release build records.

The bootstrap includes the [upstream MSYS2 runtime ASLR fix](https://github.com/msys2/MSYS2-packages/pull/6820).
Setup also enables the PE `DYNAMIC_BASE` flag in this private installation's MSYS
helper DLLs, which fixes their fork failures with Mandatory ASLR on this PC.
Windows security settings and the shared `C:/msys64` installation are unchanged.
Package archives retain their original signed bytes; the helper DLL flag change
is applied locally after installation/updates. It does not touch NOIR's DLLs.

Verification compiles and executes x64 C/C++, a Windows DLL and an MSYS host
program; it also builds and runs projects through CMake/Ninja and Meson/Ninja.
The source build uses C11/C++14 and compatible autotools wrappers for VLC 3.
MSYS Python generates POSIX makefile paths; native MinGW Python runs Meson.
The SHA256-pinned CMake 3.31.8 helper avoids incompatibilities between VLC's
older bundled CMake source and the current MinGW compiler. A JDK is also required
to build the Blu-ray Java menu libraries. `Build-Vlc.ps1` uses `-JavaHome`,
`JAVA_HOME`, or the installed JDK registry entry, in that order.

Build stages are available through:

```powershell
# Generate VLC's autotools and contrib makefiles.
& ./tools/vlc/Build-Vlc.ps1 -Stage Bootstrap
# Compile this VLC baseline's DVD libraries and run a linked x64 API probe.
& ./tools/vlc/Build-Vlc.ps1 -Stage DvdLibraries -Jobs 4
# Full development engine build, with DVD/Blu-ray explicitly required.
& ./tools/vlc/Build-Vlc.ps1 -Stage Engine -Jobs 4
# After that full build, rebuild ordinary C/C++ source fixes incrementally:
& ./tools/vlc/Build-Vlc.ps1 -Stage RebuildEngine -Jobs 4
# Use an explicit installed JDK when automatic discovery is unavailable:
# & ./tools/vlc/Build-Vlc.ps1 -Stage Engine -Jobs 4 -JavaHome C:/jdk-21
```

Outputs are isolated in `artifacts/vlc-build`; stage logs are in
`artifacts/vlc-toolchain/logs`. The DVD stage builds libdvdcss 1.4.3,
libdvdread 6.1.3 and libdvdnav 6.1.1 from the pinned contrib recipes, verifies
their upstream SHA512 checksums and applies VLC's existing dependency patches.
Its linked probe checks the compiled navigation library without opening a disc.
These static archives are build inputs, not replacement playback DLLs.
`RebuildEngine` reuses the configured engine and its compiled dependencies.
Use `Engine` again after changing the build profile, toolchain, contrib recipes,
or autotools inputs. Both modes install a complete engine into the private tree.

The engine profile retains DVD navigation/reading, Blu-ray and BD-J, codecs,
subtitles and Windows rendering/audio outputs. NOIR supplies its own WPF user
interface. The separate VLC desktop/console interfaces, visualizers, stream
output/encoders, casting discovery and SRT are omitted; NOIR already excludes
those runtime features. Digital cinema DCP and Commodore SID support are also
omitted under the agreed scope of common formats plus DVD/Blu-ray. GnuTLS,
Nettle and libarchive are explicitly built from the matching contrib recipes to avoid
mixing installed headers with static libraries from a different build.
Our image-writer patch preserves snapshots when stream output is disabled;
it creates the same encoder object without calling the disabled stream-output
helper. The existing paused MPEG snapshot regression cases cover that behavior.

The full `Engine` build and install have passed on this PC, producing the core
DLLs and 303 plug-ins. The isolated candidate has decoded the real Blu-ray in G:
with H.264 video and DTS audio, including pause, seeking and continued playback.
The updated candidate has also decoded the real DVD in G: with MPEG-2 video and
AC-3 audio. Synthetic common-format/control checks and all 88 selected NOIR
regressions pass, including the snapshot cases that failed before our native
image-writer fix. The user confirmed DVD picture, sound, menus and functional
resizing in the custom-engine player, with uneven live resizing reported.
The user clarified that the original player has the same resize lag during DVD
and Blu-ray playback; only an empty window resized smoothly. Both VLC runtimes
showed that behavior in the same interface. Fewer overlay moves did not predict
smoothness. The experimental control replacements were discarded, preserving
the established first-party Windows/VLC host and the complete custom engine.
The isolated `noir-vlc-3.0.23-20261023-event-resize` candidate adds
`0003-asynchronous-embedded-resize.patch` and matching `PlaybackView` host
notifications. VLC applies the latest parent/picture bounds on its own window
thread, independently of frame management, and updates DVD mouse coordinates
with that placement. The same application measured native geometry convergence
of 38–46 ms with the preceding engine and 5–8 ms with this patch, across playing,
paused, hardware and software cases. All 102 application checks passed with the
candidate. Its separate publication is `artifacts/vlc-validation/publish-event-resize`;
`event-resize-verification.json` records validation and remaining user checks.
The user clarified that the actual issue was continuous whole-window redraw
flicker, which this native timing change did not resolve. It remains an isolated
candidate. The successful interface fix places the native video host in a
separate, non-activating owned window, keeping GPU video redraws out of the
custom-chrome interface window. The user confirmed flicker-free DVD and Blu-ray
resizing, with working DVD menu clicks and the unchanged current VLC/AACS runtime.
All 103 application checks pass; the main-folder Release x64 build matches the
tested application and all 375 existing runtime files remain unchanged. No native engine
replacement is needed for that fix. The WindowChrome preservation experiment and
ineffective instance-level renderer override were reverted; a diagnostic that
disabled the Direct3D11 module was confined to a private publication and stopped
after it became unresponsive.

The complete custom engine has now also passed with the owned-window resize fix.
The isolated application is
`artifacts/vlc-validation/publish-custom-owned-video/NoirMediaPlayer.exe`, using
build `noir-vlc-20261023-040217` from
`artifacts/vlc-candidates/noir-vlc-3.0.23-20261023-event-resize`. All 103 application
checks and 303 module-load checks pass. Seven synthetic formats, real MPEG-2/AC-3
DVD decoding, and real H.264/DTS Blu-ray decoding passed; Blu-ray pause, seeking,
resume and continued output also passed. The user confirmed DVD picture, sound,
menu clicks and flicker-free resizing, and Blu-ray picture, sound and flicker-free
resizing. `custom-owned-video-verification.json` records the checks and packaged
file hashes; `Verify-CustomOwnedVideo.ps1` repeats the verification. The selected
main application and all 375 current runtime files remain unchanged. This test
does not activate the custom engine or install a release.
Physical track selection and Blu-ray Java menus are not covered by these results.

The regular NOIR build continues to use the tested NuGet runtime. Editing or
building source changes playback only when a complete custom engine is packaged
with `VlcRuntimeDirectory` and verified.

## Packaging a custom engine

After a successful `Engine` build, stage a new candidate and run the automated
playback checks:

```powershell
& ./tools/vlc/Stage-VlcRuntime.ps1 -Destination artifacts/vlc-candidates/my-fix
& ./tools/vlc/Test-VlcPlayback.ps1 -RuntimeDirectory artifacts/vlc-candidates/my-fix -ResultsDirectory artifacts/vlc-validation/my-fix
# With a known-working Blu-ray inserted, include a real-disc decode check:
# & ./tools/vlc/Test-VlcPlayback.ps1 -RuntimeDirectory artifacts/vlc-candidates/my-fix -ResultsDirectory artifacts/vlc-validation/my-fix -DiscSource 'bluray:///G:/'
```

Staging copies only the newly built VLC modules and their dependency DLLs,
retains the tested AACS libraries, includes both BD-J JARs and records the exact
source revision, local patch SHA256, toolchain inventory and build scripts. The
unstripped binaries stay in the build tree for debugging. Source archives and
dependency caches are kept locally; staging is not a complete public release
source bundle. Assemble that bundle before distributing a custom engine.

The probe loads every plug-in and verifies its VLC 3 entry point, then checks
the loaded engine's path, decoded video/PCM audio, MP4 seek
colors, pause/seek/resume and accepted rate changes. It exercises H.264/AAC MP4,
AAC audio, H.264 program/transport streams, MJPEG/PCM Matroska and WMV3/PCM ASF.
These synthetic streams do not establish MPEG-2 DVD or every codec's coverage.
Output goes to memory, so the main application's picture, speakers, GPU output,
disc menus and resizing still need application and real-disc checks. AACS keys
are read from the existing user configuration in place and are never copied.

Stage a runtime directory, for example `artifacts/vlc-runtime`, containing:

- `libvlc.dll`, `libvlccore.dll` and their dependent x64 DLLs.
- The complete `plugins/` tree, including `access/libdvdnav_plugin.dll`,
  `access/libdvdread_plugin.dll` and `access/liblibbluray_plugin.dll`.
- Any required `lua/` and `hrtfs/` data.
- A `licenses/` directory with the applicable notices.
- `noir-vlc-build.json` describing the source and build:

```json
{
  "schema": 1,
  "buildId": "noir-dvd-fix-1",
  "sourceRepository": "https://github.com/videolan/vlc.git",
  "sourceRevision": "REPLACE_WITH_THE_FULL_40_CHARACTER_COMMIT_SHA",
  "correspondingSource": "REPLACE_WITH_THE_SOURCE_ARCHIVE_LOCATION",
  "patches": ["patches/vlc/0001-describe-the-fix.patch"]
}
```

The revision identifies the patched HEAD; keep its source archive, patch series,
dependency sources and build instructions with the release. Retain upstream
notices and provide the applicable corresponding source when distributing changes.
The manifest records this information; the build cannot verify remote source
availability or that the declared revision produced a particular binary.

From the repository root, pass the same absolute runtime path to every .NET step:

```powershell
$runtime = (Resolve-Path artifacts/vlc-candidates/my-fix).Path
$testOutput = Join-Path (Get-Location) 'artifacts/vlc-custom-bin/'
dotnet build tests/NoirMediaPlayer.Tests -c Release -p:Platform=x64 "-p:VlcRuntimeDirectory=$runtime" "-p:BaseOutputPath=$testOutput"
dotnet test tests/NoirMediaPlayer.Tests -c Release --no-build --no-restore -p:Platform=x64 "-p:VlcRuntimeDirectory=$runtime" "-p:BaseOutputPath=$testOutput" --filter FullyQualifiedName~DvdCompatibilityRuntimeTests
dotnet publish src/NoirMediaPlayer -c Release -r win-x64 --self-contained false -p:Platform=x64 "-p:VlcRuntimeDirectory=$runtime" "-p:BaseOutputPath=$testOutput" -o artifacts/publish/custom-vlc
# Restore the standard package selection after testing the isolated build.
dotnet restore tests/NoirMediaPlayer.Tests -p:Platform=x64
```

This mode excludes both VideoLAN NuGet runtime packages. It copies the custom
runtime as a unit, rebuilds its cache, and packages the manifest/notices. Bundled
AACS DLLs remain the fallback if the custom runtime omits them; supplied custom
AACS DLLs take precedence. AACS key configuration remains unchanged. Use a fresh
build/publish output for a different engine; restore without the property to return
to the standard packages. Never stage the custom input inside a build output.

The disc runtime tests load DVD/Blu-ray modules and AACS dependencies without
opening a drive. Run ordinary playback/handoff tests too. Confirm picture, sound,
DVD menus/chapters/tracks, Blu-ray playback, pause/seeking and aspect-preserving
resize on real media before shipping a changed engine. Binary validation alone
does not establish disc playback compatibility. Keep the last tested NOIR release
available until the changed engine passes those checks.
