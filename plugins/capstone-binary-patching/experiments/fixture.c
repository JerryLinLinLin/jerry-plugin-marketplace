/* Locally compiled benign fixture. No CRT or Windows SDK headers required. */
__declspec(dllimport) void __stdcall ExitProcess(unsigned int code);
__declspec(dllimport) void *__stdcall GetStdHandle(unsigned int kind);
__declspec(dllimport) int __stdcall WriteFile(void *handle, const void *buffer,
    unsigned int length, unsigned int *written, void *overlapped);

#ifndef PATCH_VALUE
#define PATCH_VALUE 7
#endif

/* The export gives the experiment a real symbol-derived function boundary. */
__declspec(dllexport) __declspec(noinline) int patch_target(void) {
    return PATCH_VALUE;
}

void entry(void) {
    char output[] = "result=00\n";
    unsigned int written = 0;
    int result = patch_target();
    output[7] = (char)('0' + result / 10);
    output[8] = (char)('0' + result % 10);
    WriteFile(GetStdHandle((unsigned int)-11), output, 10, &written, 0);
    ExitProcess((unsigned int)result);
}
