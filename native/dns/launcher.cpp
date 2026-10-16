#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <detours.h>
#include <filesystem>
#include <iostream>
#include <string>
#include "dns_state.h"

int wmain(int argc, wchar_t **argv)
{
    if (argc < 2) { std::wcerr << L"Usage: ProxyDesk.Launcher.exe <executable> [argument-string]\n"; return 2; }
    std::filesystem::path executable = std::filesystem::absolute(argv[1]);
    if (!std::filesystem::is_regular_file(executable)) { std::wcerr << L"Executable does not exist.\n"; return 2; }
    wchar_t launcher[32768]; GetModuleFileNameW(nullptr, launcher, 32768);
    auto dll = std::filesystem::path(launcher).parent_path() / PD_DNS_DLL64;
    auto dll_string = dll.string();
    std::wstring command = L"\"" + executable.wstring() + L"\"";
    if (argc > 2) command += L" " + std::wstring(argv[2]);
    STARTUPINFOW startup{}; startup.cb = sizeof(startup); PROCESS_INFORMATION process{};
    if (!DetourCreateProcessWithDllExW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr,
        executable.parent_path().c_str(), &startup, &process, dll_string.c_str(), nullptr)) {
        std::wcerr << L"Managed launch failed. Windows error: " << GetLastError() << L"\n"; return 1;
    }
    std::wcout << process.dwProcessId << L"\n";
    CloseHandle(process.hThread); CloseHandle(process.hProcess); return 0;
}
