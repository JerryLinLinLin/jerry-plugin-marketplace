#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>

__declspec(dllexport) __declspec(noinline) int __cdecl demo_score(int value) {
    volatile int bias = 7;
    return value + bias;
}

__declspec(dllexport) __declspec(noinline) int __stdcall demo_fail(void) {
    SetLastError(1234);
    return 0;
}

int wmain(int argc, wchar_t **argv) {
    static const unsigned char payload[] = {0, 1, 2, 127, 128, 254, 255, 'F', 'r', 'i', 'd', 'a'};
    unsigned char buffer[64];
    DWORD count;
    ULONGLONG until;
    int seconds = argc > 2 ? _wtoi(argv[2]) : 8;
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
    if (argc < 2 || seconds < 1 || seconds > 60) return 2;
    printf("pid=%lu score=%d\n", GetCurrentProcessId(), demo_score(5));
    fflush(stdout);
    Sleep(150);
    if (argc > 3 && wcscmp(argv[3], L"crash") == 0) {
        RaiseException(0xE0424242, EXCEPTION_NONCONTINUABLE, 0, NULL);
        return 3;
    }
    until = GetTickCount64() + seconds * 1000ULL;
    do {
        HANDLE file = CreateFileW(argv[1], GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ,
                                  NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        if (file == INVALID_HANDLE_VALUE) return 4;
        if (!WriteFile(file, payload, sizeof(payload), &count, NULL)) return 5;
        SetFilePointer(file, 0, NULL, FILE_BEGIN);
        if (!ReadFile(file, buffer, sizeof(buffer), &count, NULL)) return 6;
        CloseHandle(file);
        OutputDebugStringW(L"Frida Use \u6d4b\u8bd5");
        Sleep(100);
    } while (GetTickCount64() < until);
    return 0;
}
