#!/usr/bin/env bash
set -euo pipefail
repo=$(cygpath -u "$NOIR_VLC_REPO")
source="$repo/vendor/vlc"
output="$repo/artifacts/vlc-build"
stage=${1:?Missing stage}
jobs=${2:?Missing parallel job limit}
case "$stage" in Bootstrap|DvdLibraries|Engine|RebuildEngine) ;; *) exit 2 ;; esac
[[ "$jobs" =~ ^[0-9]+$ ]] && (( jobs >= 1 && jobs <= 32 ))
# VLC 3 uses the compatible autotools versions bundled by the MSYS2 wrappers.
export WANT_AUTOCONF=2.71 WANT_AUTOMAKE=1.16
export PATH="$output/tools/build/bin:$(cygpath -u "$NOIR_VLC_CMAKE")/bin:/mingw64/bin:/usr/bin:/bin"
export LC_ALL=C
export BUILDCC=/usr/bin/gcc
export CC=x86_64-w64-mingw32-gcc CXX=x86_64-w64-mingw32-g++
export WIDL=widl
export CFLAGS='-O2 -std=gnu11'
export CXXFLAGS='-O2 -std=gnu++14'
export USE_FFMPEG=1
export CMAKE_BUILD_PARALLEL_LEVEL="$jobs"
export VLC_TOOLS="$output/tools/build"
finish_engine() {
    make -k -j"$jobs"
    make package-win-install
    for module in libdvdnav libdvdread liblibbluray; do
        test -s "$output/install/lib/vlc/plugins/access/${module}_plugin.dll" || {
            echo "Required disc module missing: $module" >&2; return 1;
        }
    done
    echo 'Isolated VLC engine build completed; NOIR still uses the tested runtime.'
}
# Source-only fixes can use the existing configured engine. Changes to the
# toolchain, build profile, dependencies or autotools inputs need Engine instead.
if [ "$stage" = RebuildEngine ]; then
    for required in Makefile config.status config.h; do
        test -s "$output/engine/$required" || { echo 'Run Engine before RebuildEngine' >&2; exit 1; }
    done
    export PATH="$output/x86_64-w64-mingw32/bin:$PATH"
    cd "$output/engine"
    finish_engine
    exit 0
fi
# Blu-ray Java menus use a JDK, not the Java launcher shim on Windows PATH.
if [ -n "${JAVA_HOME:-}" ]; then
    export PATH="$(cygpath -u "$JAVA_HOME")/bin:$PATH"
fi
mkdir -p "$output/tools" "$output/contrib"
cd "$output/tools"
"$source/extras/tools/bootstrap"
# Bootstrap checks the system tools. The engine stage builds any pinned helpers
# needed by upstream (including its patched Meson) before compiling dependencies.
if [ "$stage" = Engine ]; then
    NEEDED=meson "$source/extras/tools/bootstrap"
    make -j"$jobs" MAKEFLAGS="-j$jobs" CMAKEFLAGS="--parallel=$jobs"
fi
cd "$source"
bootstrap_stamp="$output/bootstrap.sha256"
bootstrap_hash=$(
    { sha256sum "$repo/tools/vlc/toolchain.lock.json";
      git ls-files -z -- bootstrap configure.ac 'Makefile.am' '**/Makefile.am' 'm4/*' | xargs -0 sha256sum;
    } | sha256sum | cut -d' ' -f1
)
# autopoint refreshes this tracked auxiliary file with the installed gettext
# version. Preserve its pre-build contents, including any intentional user edit.
if [ ! -f configure ] || [ ! -f "$bootstrap_stamp" ] || [ "$(cat "$bootstrap_stamp")" != "$bootstrap_hash" ]; then (
    saved_rpath=$(mktemp "$output/config-rpath.XXXXXX")
    cp autotools/config.rpath "$saved_rpath"
    trap 'cp "$saved_rpath" "$source/autotools/config.rpath"; rm -f "$saved_rpath"' EXIT
    ./bootstrap
)
    printf '%s\n' "$bootstrap_hash" > "$bootstrap_stamp"
fi
cd "$output/contrib"
# This bootstrap uses Python for POSIX relative paths in its generated makefile.
# Keep that one command on MSYS Python; Meson uses native MinGW Python later.
PATH="/usr/bin:/mingw64/bin:/bin" "$source/contrib/bootstrap" --host=x86_64-w64-mingw32 \
    --prefix="$output/x86_64-w64-mingw32" \
    --disable-qt --disable-qtsvg --disable-qtdeclarative \
    --disable-qtgraphicaleffects --disable-qtquickcontrols2 \
    --disable-wix --disable-wixlzx --disable-goom --disable-projectM \
    --disable-mfx --disable-sout --disable-gettext --disable-srt --disable-upnp \
    --disable-asdcplib --disable-sidplay2 --enable-gnutls --enable-nettle --enable-libarchive
# Keep contrib builds and engine builds on the same ABI. These are makefile
# values, not shell fragments; paths containing spaces are rejected below.
case "$output" in *' '*) echo 'VLC contrib requires a path without spaces' >&2; exit 1 ;; esac
cat > config.mak <<'EOF'
CC := x86_64-w64-mingw32-gcc
CXX := x86_64-w64-mingw32-g++
WIDL := widl
CFLAGS := -O2 -std=gnu11
CXXFLAGS := -O2 -std=gnu++14
EOF
make list
case "$stage" in
    Bootstrap) exit 0 ;;
    DvdLibraries)
        # Build the DVD code used by this VLC baseline, including its upstream
        # robustness patches. Never substitute these static archives into NOIR.
        make -j"$jobs" .dvdcss .dvdread .dvdnav \
            VIDEOLAN=https://downloads.videolan.org/pub/videolan \
            CONTRIB_VIDEOLAN=https://downloads.videolan.org/pub/contrib \
            GNU=https://ftpmirror.gnu.org/gnu
        for library in dvdcss dvdread dvdnav; do
            test -s "$output/x86_64-w64-mingw32/lib/lib${library}.a"
        done
        # Link and run actual library code, rather than checking file existence.
        prefix="$output/x86_64-w64-mingw32"
        "$CC" -std=gnu11 "$repo/tools/vlc/probes/dvd.c" \
            -I"$prefix/include" -L"$prefix/lib" -ldvdnav -ldvdread -ldvdcss \
            -o "$output/dvd-library-probe.exe"
        "$output/dvd-library-probe.exe"
        echo 'Pinned DVD libraries compiled successfully for Windows x64.'
        exit 0
        ;;
esac
# Use this release's pinned contrib recipes, rather than current distro codec
# packages or an unrelated nightly prebuilt. Failed dependencies stop the build.
make -k -j"$jobs" MESON_BUILD="--jobs=$jobs" \
    VIDEOLAN=https://downloads.videolan.org/pub/videolan \
    CONTRIB_VIDEOLAN=https://downloads.videolan.org/pub/contrib \
    GNU=https://ftpmirror.gnu.org/gnu
export PATH="$output/x86_64-w64-mingw32/bin:$PATH"
export CXXFLAGS="$CXXFLAGS -static-libgcc -static-libstdc++"
export LDFLAGS='-static-libgcc'
export CPPFLAGS='-D__MSVCRT_VERSION__=0x600 -D_WIN32_WINNT=0x0601 -DWINVER=0x0601'
mkdir -p "$output/engine"
cd "$output/engine"
"$source/extras/package/win32/configure.sh" \
    --host=x86_64-w64-mingw32 --with-contrib="$output/x86_64-w64-mingw32" \
    --prefix="$output/install" --disable-vlc --disable-qt --disable-skins2 --disable-ncurses \
    --disable-update-check --disable-nls \
    --disable-goom --disable-projectm --disable-qsv --disable-sout --disable-shout \
    --disable-srt --disable-upnp --disable-asdcp --disable-sid \
    --enable-dvdread --enable-dvdnav --enable-bluray
finish_engine
