#!/usr/bin/env bash
set -euo pipefail
repo=$(cygpath -u "$NOIR_VLC_REPO")
toolchain="$repo/artifacts/vlc-toolchain"
export PATH="$(cygpath -u "$NOIR_VLC_CMAKE")/bin:/mingw64/bin:/usr/bin:/bin"
export LC_ALL=C
operation=${1:?Missing operation}
shift
case "$operation" in
    keyring)
        pacman-key --init
        pacman-key --populate msys2
        ;;
    update) pacman -Syu --noconfirm ;;
    install) pacman -S --needed --noconfirm "$@" ;;
    verify)
        for package in "$@"; do pacman -Q "$package" >/dev/null; done
        for command in gcc g++ make autoconf automake autoreconf libtool pkg-config \
            cmake meson ninja nasm yasm bison flex gperf patch git python widl; do
            command -v "$command" >/dev/null || { echo "Missing $command" >&2; exit 1; }
        done
        test "$(x86_64-w64-mingw32-gcc -dumpmachine)" = x86_64-w64-mingw32
        case "$(/usr/bin/gcc -dumpmachine)" in
            x86_64-pc-msys|x86_64-pc-cygwin) ;;
            *) echo 'Unexpected MSYS host compiler target' >&2; exit 1 ;;
        esac
        probe="$toolchain/probe"
        mkdir -p "$probe"
        cd "$probe"
        x86_64-w64-mingw32-gcc -std=gnu11 -Wall -Wextra -Werror -shared \
            "$repo/tools/vlc/probes/library.c" -o noir-vlc-probe.dll
        x86_64-w64-mingw32-gcc -std=gnu11 -Wall -Wextra -Werror \
            "$repo/tools/vlc/probes/native.c" -o native-c.exe
        x86_64-w64-mingw32-g++ -std=gnu++14 -Wall -Wextra -Werror -static-libgcc -static-libstdc++ \
            "$repo/tools/vlc/probes/native.cpp" -o native-cpp.exe
        /usr/bin/gcc -std=gnu11 -Wall -Wextra -Werror \
            "$repo/tools/vlc/probes/host.c" -o host-c.exe
        ./native-c.exe
        ./native-cpp.exe
        ./host-c.exe
        for binary in noir-vlc-probe.dll native-c.exe native-cpp.exe; do
            /mingw64/bin/objdump -f "$binary" | grep -q 'pei-x86-64'
        done
        # Exercise POSIX forks and both Perl/Python helpers used by autotools.
        for n in $(seq 1 20); do (uname >/dev/null); done
        perl -e 'print "Perl helper passed\n"'
        python -c 'print("Python helper passed")'
        meson --version
        cmake --version
        cmake -S "$repo/tools/vlc/probes" -B "$probe/cmake" -G Ninja
        cmake --build "$probe/cmake"
        "$probe/cmake/noir-cmake-probe.exe"
        if [ -f "$probe/meson/meson-private/coredata.dat" ]; then
            meson setup --reconfigure "$probe/meson" "$repo/tools/vlc/probes"
        else
            meson setup "$probe/meson" "$repo/tools/vlc/probes"
        fi
        meson compile -C "$probe/meson"
        meson test -C "$probe/meson" --print-errorlogs
        pacman -Q > "$toolchain/packages.txt"
        x86_64-w64-mingw32-gcc --version
        echo 'Toolchain verification passed (x64 C/C++/DLL, host compiler, forks).'
        ;;
    *) echo "Unknown operation: $operation" >&2; exit 2 ;;
esac
