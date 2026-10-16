#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <filesystem>
#include <iostream>
#include <memory>
#include <string>
#include "dns_state.h"

using Handle = std::unique_ptr<void, decltype(&CloseHandle)>;

static uintptr_t module_base(DWORD pid, const std::wstring &path, bool exact_path)
{
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid), CloseHandle);
    if (snapshot.get() == INVALID_HANDLE_VALUE) throw GetLastError();
    MODULEENTRY32W entry{}; entry.dwSize = sizeof(entry);
    if (!Module32FirstW(snapshot.get(), &entry)) throw GetLastError();
    do {
        if (_wcsicmp(exact_path ? entry.szExePath : entry.szModule, path.c_str()) == 0)
            return reinterpret_cast<uintptr_t>(entry.modBaseAddr);
    } while (Module32NextW(snapshot.get(), &entry));
    if (GetLastError() != ERROR_NO_MORE_FILES) throw GetLastError();
    return 0;
}

static DWORD run_thread(HANDLE process, uintptr_t entry, void *argument)
{
    Handle thread(CreateRemoteThread(process, nullptr, 0, reinterpret_cast<LPTHREAD_START_ROUTINE>(entry), argument, 0, nullptr), CloseHandle);
    if (!thread) throw GetLastError();
    auto wait = WaitForSingleObject(thread.get(), 10000);
    if (wait != WAIT_OBJECT_0) throw wait == WAIT_TIMEOUT ? DWORD(ERROR_TIMEOUT) : GetLastError();
    DWORD result;
    if (!GetExitCodeThread(thread.get(), &result)) throw GetLastError();
    return result;
}

int wmain(int argc, wchar_t **argv)
{
    if (argc != 3) { std::wcerr << L"Usage: ProxyDesk.Injector.exe <pid> <creation-filetime>\n"; return 2; }
    try {
        auto pid = std::stoul(argv[1]); auto expected_creation = std::stoull(argv[2]);
        if (pid == 0 || pid == GetCurrentProcessId()) throw DWORD(ERROR_INVALID_PARAMETER);
        Handle process(OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_CREATE_THREAD | PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, FALSE, pid), CloseHandle);
        if (!process) throw GetLastError();
        FILETIME creation, exit, kernel, user;
        if (!GetProcessTimes(process.get(), &creation, &exit, &kernel, &user)) throw GetLastError();
        auto actual_creation = (static_cast<ULONGLONG>(creation.dwHighDateTime) << 32) | creation.dwLowDateTime;
        if (actual_creation != expected_creation) throw DWORD(ERROR_INVALID_PARAMETER);
        USHORT machine, native_machine;
        if (!IsWow64Process2(process.get(), &machine, &native_machine)) throw GetLastError();
        if (native_machine != IMAGE_FILE_MACHINE_AMD64 || (sizeof(void *) == 4) != (machine == IMAGE_FILE_MACHINE_I386))
            throw DWORD(ERROR_BAD_EXE_FORMAT);
        wchar_t self[32768];
        if (!GetModuleFileNameW(nullptr, self, 32768)) throw GetLastError();
        auto dll = std::filesystem::path(self).parent_path() / (sizeof(void *) == 8 ? PD_DNS_DLL64 : PD_DNS_DLL32);
        auto path = dll.wstring();
        auto remote_module = module_base(pid, path, true);
        if (!remote_module) {
            auto loader = GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW");
            HMODULE loader_module;
            if (!loader || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCWSTR>(loader), &loader_module)) throw GetLastError();
            wchar_t loader_path[32768];
            if (!GetModuleFileNameW(loader_module, loader_path, 32768)) throw GetLastError();
            auto remote_loader = module_base(pid, std::filesystem::path(loader_path).filename().wstring(), false);
            if (!remote_loader) throw DWORD(ERROR_MOD_NOT_FOUND);
            auto bytes = (path.size() + 1) * sizeof(wchar_t);
            auto argument = VirtualAllocEx(process.get(), nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (!argument) throw GetLastError();
            bool completed = false;
            try {
                if (!WriteProcessMemory(process.get(), argument, path.c_str(), bytes, nullptr)) throw GetLastError();
                run_thread(process.get(), remote_loader + reinterpret_cast<uintptr_t>(loader) - reinterpret_cast<uintptr_t>(loader_module), argument);
                completed = true;
            } catch (DWORD error) {
                // A timed out loader may still read its argument; keep that allocation valid.
                if (error != ERROR_TIMEOUT) VirtualFreeEx(process.get(), argument, 0, MEM_RELEASE);
                throw;
            }
            if (completed) VirtualFreeEx(process.get(), argument, 0, MEM_RELEASE);
            remote_module = module_base(pid, path, true);
            if (!remote_module) throw DWORD(ERROR_DLL_INIT_FAILED);
        }
        auto local_module = LoadLibraryExW(path.c_str(), nullptr, DONT_RESOLVE_DLL_REFERENCES);
        if (!local_module) throw GetLastError();
        auto initialize = GetProcAddress(local_module, "ProxyDeskInitialize");
        auto offset = reinterpret_cast<uintptr_t>(initialize) - reinterpret_cast<uintptr_t>(local_module);
        FreeLibrary(local_module);
        if (!initialize) throw DWORD(ERROR_PROC_NOT_FOUND);
        auto result = run_thread(process.get(), remote_module + offset, nullptr);
        if (result != ERROR_SUCCESS) throw result;
        return 0;
    } catch (DWORD error) {
        std::wcerr << L"Automatic application DNS failed. Windows error: " << error << L"\n";
    } catch (const std::exception &error) { std::cerr << error.what() << "\n"; }
    return 1;
}
