#include <iostream>
#include <vector>

int main()
{
    const std::vector<int> values {16, 48};
    if (sizeof(void *) != 8 || values[0] + values[1] != 64)
        return 1;
    std::cout << "Windows x64 C++ passed\n";
}
