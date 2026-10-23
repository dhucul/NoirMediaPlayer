#include <stdio.h>

int main(void)
{
    puts("MSYS host compiler passed");
    return sizeof(void *) == 8 ? 0 : 1;
}
