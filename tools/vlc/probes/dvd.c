#include <stdio.h>
#include <string.h>
#include <dvdnav/dvdnav.h>

int main(void)
{
    const char *version = dvdnav_version();
    if (!version || strcmp(version, "6.1.1") || sizeof(void *) != 8)
        return 1;
    printf("Windows x64 libdvdnav %s linked and ran successfully\n", version);
    return 0;
}
