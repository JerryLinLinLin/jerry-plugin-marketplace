#include <windows.h>
#include <dbghelp.h>
#include <stdio.h>

/* A benign, self-contained fixture. It only dumps its own process. */
__declspec(dllexport) __declspec(noinline) int bundle_fixture(int value) {
    return value * 3 + 7;
}
const char bundle_marker[] = "RIZIN_BUNDLE_SMOKE_2026";

int main(int argc, char **argv) {
    printf("%s: %d\n", bundle_marker, bundle_fixture(5));
    if (argc == 2) {
        HANDLE file = CreateFileA(argv[1], GENERIC_WRITE, 0, NULL, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, NULL);
        if (file == INVALID_HANDLE_VALUE) return 2;
        BOOL ok = MiniDumpWriteDump(GetCurrentProcess(), GetCurrentProcessId(), file,
            MiniDumpWithFullMemory | MiniDumpWithFullMemoryInfo | MiniDumpWithThreadInfo,
            NULL, NULL, NULL);
        CloseHandle(file);
        if (!ok) return 3;
    }
    return 0;
}
