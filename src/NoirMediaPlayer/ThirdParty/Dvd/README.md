# DVD playback plug-ins

NOIR uses two unmodified x64 VideoLAN DVD plug-ins for DVD menu/navigation access (`libdvdnav_plugin.dll`) and DVD title reading (`libdvdread_plugin.dll`). DVD playback remains on VLC. Supported ordinary media continues to prefer Windows playback. These plug-ins do not replace or change the Blu-ray/AACS runtime.

Build and publish restore these two files from the official `VideoLAN.LibVLC.Windows.GPL` NuGet package. Both it and `VideoLAN.LibVLC.Windows` use the single `VlcRuntimeVersion` property in the app project, currently 3.0.23.1. The GPL package's build targets and other modules are excluded. The older DLL copies in this source folder are historical copies; packaging now uses the restored package, so they cannot override a future runtime update.

Update `VlcRuntimeVersion` when preparing a NOIR release, review the corresponding package/source notices below, and verify the packaged modules and physical DVD/Blu-ray playback before shipping. Users receive the engine through the NOIR update. Updating a standalone VLC installation does not change NOIR's bundled runtime.

An explicit `VlcRuntimeDirectory` build uses a complete custom VLC runtime instead of either VideoLAN package. Its DVD modules come from that same build; this default-package provenance table does not describe custom binaries. Custom source/build information and notices are included under `licenses/vlc`. See `tools/vlc/README.md` in the repository for maintaining and shipping NOIR engine fixes.

## Binary provenance

Package: [VideoLAN.LibVLC.Windows.GPL 3.0.23.1](https://www.nuget.org/packages/VideoLAN.LibVLC.Windows.GPL/3.0.23.1).

Download: https://api.nuget.org/v3-flatcontainer/videolan.libvlc.windows.gpl/3.0.23.1/videolan.libvlc.windows.gpl.3.0.23.1.nupkg

Package SHA-256: `6fd84d5ee58acb9d42ecd6fcc0e1cc706eabb8c896ad09cb87e588472f333603`.

The NuGet.org repository signature was successfully verified with `dotnet nuget verify --all`; its signed owner is `videolan`.

| File | Original package entry | SHA-256 |
| --- | --- | --- |
| `libdvdnav_plugin.dll` | `build/x64/plugins/access/libdvdnav_plugin.dll` | `e978244ebc0ae7b3eee91799c3c1b9dabc9760ea9ff937285d5233b99d0b73a1` |
| `libdvdread_plugin.dll` | `build/x64/plugins/access/libdvdread_plugin.dll` | `27c2d191b6257d99ae3810b88470279d0acefc0065f82719c2f2ca325e742bfe` |

## Licenses and upstream sources

The binary package declares **GPL-2.0-or-later**. VLC's DVD access wrappers include LGPL-2.1-or-later source notices and explain that linking the DVD libraries makes the resulting plug-ins GPL. Both unmodified upstream license texts are in `licenses/` and are copied, with this notice, to `licenses/dvd` in builds and publishes. The replaceable plug-ins are copied to `libvlc/win-x64/plugins/access`.

Upstream source release, including the module sources, build scripts and DVD dependency recipes:

- https://download.videolan.org/pub/videolan/vlc/3.0.23/vlc-3.0.23.tar.xz
- SHA-256: `e891cae6aa3ccda69bf94173d5105cbc55c7a7d9b1d21b9b21666e69eff3e7e0`
- Module sources: `modules/access/dvdnav.c`, `modules/access/dvdread.c`.
- Dependency recipes: `contrib/src/dvdnav`, `contrib/src/dvdread`, `contrib/src/dvdcss`.
- Package build project: https://code.videolan.org/videolan/libvlc-nuget

The source release's DVD dependency recipes reference these upstream releases:

- https://download.videolan.org/pub/videolan/libdvdnav/6.1.1/libdvdnav-6.1.1.tar.bz2
- https://download.videolan.org/pub/videolan/libdvdread/6.1.3/libdvdread-6.1.3.tar.bz2
- https://download.videolan.org/pub/videolan/libdvdcss/1.4.3/libdvdcss-1.4.3.tar.bz2

These components remain required third-party DVD playback code. The native replacement effort has stopped; retain these modules and their notices in NOIR releases.
