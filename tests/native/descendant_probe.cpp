#define WIN32_LEAN_AND_MEAN
#define UNICODE
#define _UNICODE
#include <winsock2.h>
#include <windows.h>
#include <windns.h>
#include <filesystem>
#include <fstream>
#include <string>
#pragma comment(lib, "dnsapi.lib")

int wmain(int argc, wchar_t **argv)
{
    if (argc >= 4 && wcscmp(argv[1], L"--parent") == 0) {
        std::wstring command = L"\"" + std::wstring(argv[2]) + L"\" --child \"" + argv[3] + L"\"";
        if (argc > 4) command += L" \"" + std::wstring(argv[4]) + L"\"";
        STARTUPINFOW startup{}; startup.cb = sizeof(startup); PROCESS_INFORMATION child{};
        if (!CreateProcessW(argv[2], command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW,
            nullptr, nullptr, &startup, &child)) return 2;
        CloseHandle(child.hThread); CloseHandle(child.hProcess); return 0;
    }
    if (argc < 3 || wcscmp(argv[1], L"--child") != 0) return 2;
    std::wstring output = argv[2];
    { std::ofstream ready{std::filesystem::path(output + L".ready.tmp")}; ready << GetCurrentProcessId(); }
    std::filesystem::rename(output + L".ready.tmp", output + L".ready");
    auto deadline = GetTickCount64() + 20000;
    while (!std::filesystem::exists(output + L".go")) {
        if (GetTickCount64() >= deadline) return 3;
        Sleep(10);
    }
    bool loaded = GetModuleHandleW(sizeof(void *) == 8 ? L"ProxyDeskDnsTests64.dll" : L"ProxyDeskDnsTests32.dll") != nullptr;
    PDNS_RECORDW records = nullptr;
    auto status = DnsQuery_W(L"descendant-audit.fixture.invalid", DNS_TYPE_A, DNS_QUERY_NO_WIRE_QUERY,
        nullptr, &records, nullptr);
    if (records) DnsRecordListFree(records, DnsFreeRecordList);
    { std::ofstream result{std::filesystem::path(output + L".result")}; result << "loaded=" << loaded
        << "\nproduction_loaded=" << (GetModuleHandleW(sizeof(void *) == 8 ? L"ProxyDeskDns64.dll" : L"ProxyDeskDns32.dll") != nullptr) << "\ndns=" << status << "\n"; }
    return 0;
}
