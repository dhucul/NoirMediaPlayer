#include <windows.h>
#include <stdio.h>

int main(void)
{
    HMODULE module = LoadLibraryW(L"noir-vlc-probe.dll");
    if (!module)
        return 1;
    FARPROC address = GetProcAddress(module, "noir_probe");
    /* Windows function pointers have the same representation for this ABI. */
    union { FARPROC address; int (*call)(void); } function = { address };
    int result = address && function.call() == 64 ? 0 : 2;
    FreeLibrary(module);
    if (!result)
        puts("Windows x64 C/DLL load passed");
    return result;
}
