#include <windows.h>

__declspec(dllexport) int noir_probe(void)
{
    return sizeof(void *) == 8 ? 64 : 0;
}
