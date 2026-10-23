# NOIR engine fixes

The editable VLC checkout is `vendor/vlc`, on the local `noir/fixes` branch.
`tools/vlc/source.lock.json` records its upstream baseline; the checkout itself is
ignored by the parent NOIR repository. Keep our portable fixes in this directory
so they can be reviewed and restored with NOIR's source.

For a tracked working-tree fix, from the NOIR repository root:

```powershell
git -C vendor/vlc diff --binary --full-index --no-ext-diff --no-textconv --output=../../patches/vlc/0001-describe-the-fix.patch refs/noir/upstream-base --
```

This includes tracked changes since the upstream baseline, including local
commits. Use `git -C vendor/vlc add -N <new-source-file>` first for an intended new
file so it is included in the diff. Review the patch; do not include generated
build output. For several independent fixes, keep an ordered series of patches
or export individual local commits with `git format-patch`.

On a fresh pinned checkout, check and apply a working-tree patch explicitly:

```powershell
git -C vendor/vlc apply --check ../../patches/vlc/0001-describe-the-fix.patch
git -C vendor/vlc apply ../../patches/vlc/0001-describe-the-fix.patch
```

Keep the regression test, explanation and any dependency-source changes with
each fix. Record the patched revision/source archive in the custom runtime's
build manifest when distributing it. No engine patch is applied by source setup,
and source edits do not change NOIR's installed playback engine automatically.

`0001-native-build-compat.patch` contains the local build fixes for the pinned
VLC baseline: pass the selected C standard to the legacy regex makefile,
exclude a dangling web-server favicon symlink from the VNC client source
extraction on Windows, make explicitly enabled source dependencies take
precedence over installed versions, and disable GnuTLS's developer-only GCC
warning/analyzer suite. Libarchive is configured to use static zlib so its
headers and final VLC link agree. These fixes do not change DVD/Blu-ray
playback logic. The GME pkg-config file also leaves GCC runtime selection to
VLC's static link flags, avoiding an unnecessary shared-unwinder dependency.

`0002-snapshot-without-stream-output.patch` keeps image snapshots available in
the playback-only engine. The image writer creates its encoder object directly,
as the stream-output helper does, instead of calling that helper's disabled-build
stub. PNG/JPEG image encoders remain available without enabling stream output.
Regression coverage is in `NativeMpeg_SeekAndCaptureHandoffRetainPauseAudioAndRate`
(the paused `.mpg` and `.vob` capture cases).

`0003-asynchronous-embedded-resize.patch` moves video child-window placement to
VLC's window thread. `PlaybackView` posts `NOIR.VLC.EmbeddedResize.v1` when its
Win32 host changes bounds. The engine reads the latest parent size, fits the
picture using the current source/aspect/zoom configuration, and updates the
mouse-coordinate mapping used by DVD menus. The UI does not wait for a frame or
make a synchronous call to VLC's window thread. Rendering-resource updates
continue through VLC's existing display management. Stock VLC safely ignores the
notification; other VLC embedders retain the normal geometry polling path.

The patch is paired with `PlaybackResizeTests.cs`, which checks the actual native
picture bounds during playback and pause with hardware decoding enabled and
disabled, including compact/fullscreen transitions. Optional
`NOIR_RESIZE_METRICS_DIR` reports measure geometry convergence. These measurements
do not establish perceived drag smoothness; validate picture, sound and DVD menu
interaction in the isolated candidate before replacing the selected runtime.
The user confirmed that the native timing improvement did not resolve the
whole-window redraw flicker. That issue was resolved in `PlaybackView` by
separating the video surface from the custom-chrome interface window, using the
existing VLC runtime. This native patch remains an unselected development fix.
