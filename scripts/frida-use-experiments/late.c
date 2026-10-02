#include <windows.h>
__declspec(dllexport) __declspec(noinline) int __cdecl late_value(int value) { return value * 2; }
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved) { return TRUE; }
