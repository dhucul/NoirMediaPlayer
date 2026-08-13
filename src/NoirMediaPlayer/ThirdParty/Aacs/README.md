# Bundled AACS runtime

NOIR dynamically links the following unmodified 64-bit UCRT binaries maintained by MSYS2:

- VideoLAN libaacs 0.11.1-2 (`libaacs-0.dll`, distributed as `libaacs.dll`), LGPL
- GNU libgcrypt 1.12.2-2 (`libgcrypt-20.dll`), LGPL
- GNU libgpg-error 1.61-1 (`libgpg-error-0.dll`), LGPL-2.1-or-later

The corresponding license texts are in this directory's `licenses` folder and are copied to `licenses/aacs` in application builds. The libraries remain replaceable by the user.

## Binary provenance

| Package | MSYS2 binary package | SHA-256 |
| --- | --- | --- |
| libaacs | [mingw-w64-ucrt-x86_64-libaacs-0.11.1-2](https://mirror.msys2.org/mingw/ucrt64/mingw-w64-ucrt-x86_64-libaacs-0.11.1-2-any.pkg.tar.zst) | `ef184086b2b34f670631bbafecb7951b389acc15dcf2b343e6d146350f09f1a6` |
| libgcrypt | [mingw-w64-ucrt-x86_64-libgcrypt-1.12.2-2](https://mirror.msys2.org/mingw/ucrt64/mingw-w64-ucrt-x86_64-libgcrypt-1.12.2-2-any.pkg.tar.zst) | `2fd94260dbbe258a978c6e30f5bd3dcc79963f61595129c39c9b968af2a64086` |
| libgpg-error | [mingw-w64-ucrt-x86_64-libgpg-error-1.61-1](https://mirror.msys2.org/mingw/ucrt64/mingw-w64-ucrt-x86_64-libgpg-error-1.61-1-any.pkg.tar.zst) | `ca3045875ddae643e6aa927406b8be49670bcdbf31e7c35651bbacf555e95421` |

Corresponding source packages are available from the MSYS2 source repository:

- https://mirror.msys2.org/mingw/sources/mingw-w64-libaacs-0.11.1-2.src.tar.zst
- https://mirror.msys2.org/mingw/sources/mingw-w64-libgcrypt-1.12.2-2.src.tar.zst
- https://mirror.msys2.org/mingw/sources/mingw-w64-libgpg-error-1.61-1.src.tar.zst

libaacs does not include decryption keys or certificates. A compatible, lawfully obtained `KEYDB.cfg` remains necessary for protected media.
