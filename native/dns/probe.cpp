#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <windns.h>
#include <fstream>
#include <filesystem>
#include <string>
#include "dns_state.h"
#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "dnsapi.lib")
static HANDLE completion;
static DWORD completed_status;
static void WINAPI dns_done(void *, PDNS_QUERY_RESULT result) { completed_status = result->QueryStatus; if (result->pQueryRecords) DnsRecordListFree(result->pQueryRecords, DnsFreeRecordList); SetEvent(completion); }
int wmain(int argc, wchar_t **argv)
{
    if (argc < 2) return 2;
    bool live_dns = argc > 2 && wcscmp(argv[2], L"--live-dns") == 0;
    bool stopped_dns = argc > 2 && wcscmp(argv[2], L"--stopped-dns") == 0;
    bool runtime_attach = live_dns || (argc > 2 && wcscmp(argv[2], L"--wait-for-dns") == 0);
    if (runtime_attach) {
        std::ofstream ready(std::filesystem::path(std::wstring(argv[1]) + L".ready")); ready.close();
        auto deadline = GetTickCount64() + 15000;
        while (!std::filesystem::exists(std::wstring(argv[1]) + L".run")) {
            if (GetTickCount64() >= deadline) return 2;
            Sleep(20);
        }
    }
    WSADATA data; WSAStartup(MAKEWORD(2, 2), &data);
    std::ofstream output{std::filesystem::path(argv[1])}; bool passed = true;
    auto check = [&](bool valid, const char *name) { output << (valid ? "PASS: " : "FAIL: ") << name << "\n"; passed &= valid; };
    check(GetModuleHandleW(sizeof(void *) == 8 ? PD_DNS_DLL64 : PD_DNS_DLL32) != nullptr,
        runtime_attach ? "DNS component attached to a normally started application" : "DNS component loaded before application code");
    if (stopped_dns) {
        auto event = OpenEventW(EVENT_MODIFY_STATE, FALSE, PD_DNS_BYPASS_EVENT);
        check(event && SetEvent(event), "Explicit stop disables application DNS interception");
        PDNS_RECORDW records = nullptr;
        auto status = DnsQuery_W(L"stopped.fixture.invalid", DNS_TYPE_A, DNS_QUERY_NO_WIRE_QUERY, nullptr, &records, nullptr);
        check(status != ERROR_SUCCESS && status != ERROR_GEN_FAILURE, "Stopped DNS uses the system cache instead of the broker fixture");
        if (records) DnsRecordListFree(records, DnsFreeRecordList);
        if (event) { ResetEvent(event); CloseHandle(event); }
        output << (passed ? "STOPPED_DNS_PASS" : "STOPPED_DNS_FAIL") << "\n";
        output.close();
        std::ofstream done(std::filesystem::path(std::wstring(argv[1]) + L".done")); done << "done";
        WSACleanup(); return passed ? 0 : 1;
    }
    if (live_dns) {
        PDNS_RECORDW records = nullptr;
        auto status = DnsQuery_W(L"example.com", DNS_TYPE_A, DNS_QUERY_BYPASS_CACHE, nullptr, &records, nullptr);
        check(status == ERROR_SUCCESS && records != nullptr, "Normal application DNS returns a real upstream answer");
        output << "DNS_STATUS: " << status << "\n";
        bool connected = false;
        for (auto record = records; record && !connected; record = record->pNext) {
            if (record->wType != DNS_TYPE_A) continue;
            auto socket_handle = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
            if (socket_handle == INVALID_SOCKET) continue;
            u_long nonblocking = 1; ioctlsocket(socket_handle, FIONBIO, &nonblocking);
            sockaddr_in target{}; target.sin_family = AF_INET; target.sin_port = htons(80); target.sin_addr.s_addr = record->Data.A.IpAddress;
            auto result = connect(socket_handle, reinterpret_cast<sockaddr *>(&target), sizeof(target));
            if (result == 0) connected = true;
            else if (WSAGetLastError() == WSAEWOULDBLOCK) {
                fd_set writable, errors; FD_ZERO(&writable); FD_ZERO(&errors); FD_SET(socket_handle, &writable); FD_SET(socket_handle, &errors);
                timeval timeout{10, 0};
                if (select(0, nullptr, &writable, &errors, &timeout) > 0 && FD_ISSET(socket_handle, &writable)) {
                    int error = 0, size = sizeof(error);
                    connected = getsockopt(socket_handle, SOL_SOCKET, SO_ERROR, reinterpret_cast<char *>(&error), &size) == 0 && error == 0;
                }
            }
            if (connected) {
                nonblocking = 0; ioctlsocket(socket_handle, FIONBIO, &nonblocking);
                DWORD timeout = 10000;
                setsockopt(socket_handle, SOL_SOCKET, SO_SNDTIMEO, reinterpret_cast<char *>(&timeout), sizeof(timeout));
                setsockopt(socket_handle, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<char *>(&timeout), sizeof(timeout));
                constexpr char request[] = "GET / HTTP/1.0\r\nHost: example.com\r\n\r\n";
                char reply[256]{};
                connected = send(socket_handle, request, sizeof(request) - 1, 0) == sizeof(request) - 1 && recv(socket_handle, reply, sizeof(reply) - 1, 0) > 0 && strncmp(reply, "HTTP/", 5) == 0;
                if (connected) output << "HTTP_RESPONSE: " << std::string(reply).substr(0, std::string(reply).find("\r\n")) << "\n";
            }
            closesocket(socket_handle);
        }
        check(connected, "TCP receives an HTTP response using the resolved upstream address");
        if (records) DnsRecordListFree(records, DnsFreeRecordList);
        output << (passed ? "LIVE_DNS_PASS" : "LIVE_DNS_FAIL") << "\n";
        WSACleanup(); return passed ? 0 : 1;
    }
    PDNS_RECORDW records = nullptr;
    auto status = DnsQuery_W(L"example.invalid", DNS_TYPE_A, DNS_QUERY_BYPASS_CACHE, nullptr, &records, nullptr);
    if (status != 0) output << "DNS_STATUS: " << status << "\n";
    check(status == 0 && records && records->Data.A.IpAddress == inet_addr("203.0.113.7"), "DnsQuery_W returns the broker response");
    if (records) DnsRecordListFree(records, DnsFreeRecordList);
    PDNS_RECORD narrow_records = nullptr;
    status = DnsQuery_A("example.invalid", DNS_TYPE_AAAA, 0, nullptr, &narrow_records, nullptr);
    check(status == 0 && narrow_records && narrow_records->wType == DNS_TYPE_AAAA, "DnsQuery_A supports AAAA and record freeing");
    if (narrow_records) DnsRecordListFree(narrow_records, DnsFreeRecordList);
    ADDRINFOW hints{}; hints.ai_family = AF_UNSPEC; hints.ai_socktype = SOCK_STREAM;
    PADDRINFOW addresses = nullptr;
    auto address_status = GetAddrInfoW(L"example.invalid", L"443", &hints, &addresses);
    check(address_status == 0 && addresses && addresses->ai_next, "GetAddrInfoW returns IPv4 and IPv6 addresses"); if (addresses) FreeAddrInfoW(addresses);
    ADDRINFOA hints_a{}; hints_a.ai_family = AF_INET;
    PADDRINFOA addresses_a = nullptr;
    check(getaddrinfo("example.invalid", "80", &hints_a, &addresses_a) == 0, "getaddrinfo ANSI succeeds"); if (addresses_a) freeaddrinfo(addresses_a);
    PDNS_RECORD utf8_records = nullptr;
    status = DnsQuery_UTF8("example.invalid", DNS_TYPE_TEXT, 0, nullptr, &utf8_records, nullptr);
    check(status == 0 && utf8_records && utf8_records->wType == DNS_TYPE_TEXT, "DnsQuery_UTF8 supports TXT records");
    if (utf8_records) DnsRecordListFree(utf8_records, DnsFreeRecordList);
    check(gethostbyname("example.invalid") != nullptr, "Legacy gethostbyname uses the application DNS broker");
    ADDRINFOEXW hints_ex{}; hints_ex.ai_family = AF_INET; hints_ex.ai_socktype = SOCK_STREAM;
    PADDRINFOEXW extended = nullptr;
    check(GetAddrInfoExW(L"example.invalid", L"443", NS_DNS, nullptr, &hints_ex, &extended, nullptr, nullptr, nullptr, nullptr) == 0 && extended, "GetAddrInfoExW synchronous query");
    if (extended) FreeAddrInfoExW(extended);
    ADDRINFOEXA hints_ex_a{}; hints_ex_a.ai_family = AF_INET; hints_ex_a.ai_socktype = SOCK_STREAM;
    PADDRINFOEXA extended_a = nullptr;
    check(GetAddrInfoExA("example.invalid", "443", NS_DNS, nullptr, &hints_ex_a, &extended_a, nullptr, nullptr, nullptr, nullptr) == 0 && extended_a, "GetAddrInfoExA query and shared free function");
    if (extended_a) FreeAddrInfoEx(reinterpret_cast<PADDRINFOEXW>(extended_a));
    OVERLAPPED address_operation{}; address_operation.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr); HANDLE address_cancel = nullptr;
    extended = nullptr;
    int pending = GetAddrInfoExW(L"example.invalid", L"443", NS_DNS, nullptr, &hints_ex, &extended, nullptr, &address_operation, nullptr, &address_cancel);
    check(pending == WSA_IO_PENDING && WaitForSingleObject(address_operation.hEvent, 5000) == WAIT_OBJECT_0 && GetAddrInfoExOverlappedResult(&address_operation) == 0 && extended, "GetAddrInfoExW overlapped completion");
    if (extended) FreeAddrInfoExW(extended);
    ResetEvent(address_operation.hEvent); address_operation.Internal = 0; extended = nullptr;
    pending = GetAddrInfoExW(L"cancel.invalid", L"443", NS_DNS, nullptr, &hints_ex, &extended, nullptr, &address_operation, nullptr, &address_cancel);
    if (pending == WSA_IO_PENDING) GetAddrInfoExCancel(&address_cancel);
    check(WaitForSingleObject(address_operation.hEvent, 5000) == WAIT_OBJECT_0 && GetAddrInfoExOverlappedResult(&address_operation) == WSA_E_CANCELLED, "GetAddrInfoExCancel signals cancellation");
    CloseHandle(address_operation.hEvent);
    completion = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    DNS_QUERY_REQUEST request{}; request.Version = DNS_QUERY_REQUEST_VERSION1; request.QueryName = L"example.invalid"; request.QueryType = DNS_TYPE_A;
    request.pQueryCompletionCallback = dns_done;
    DNS_QUERY_RESULT result{}; result.Version = DNS_QUERY_RESULTS_VERSION1; DNS_QUERY_CANCEL cancel{};
    status = DnsQueryEx(&request, &result, &cancel);
    check(status == DNS_REQUEST_PENDING && WaitForSingleObject(completion, 5000) == WAIT_OBJECT_0 && completed_status == 0, "DnsQueryEx asynchronous completion");
    ResetEvent(completion); request.QueryName = L"cancel.invalid"; result = {}; result.Version = DNS_QUERY_RESULTS_VERSION1;
    status = DnsQueryEx(&request, &result, &cancel); if (status == DNS_REQUEST_PENDING) DnsCancelQuery(&cancel);
    check(WaitForSingleObject(completion, 5000) == WAIT_OBJECT_0 && completed_status == ERROR_CANCELLED, "DnsCancelQuery completes exactly once with cancellation");
    CloseHandle(completion);
    if (argc < 3 || runtime_attach) {
        wchar_t executable[32768]; GetModuleFileNameW(nullptr, executable, 32768);
        std::wstring child_output = std::wstring(argv[1]) + L".child";
        std::wstring command = L"\"" + std::wstring(executable) + L"\" \"" + child_output + L"\" child";
        STARTUPINFOW startup{}; startup.cb = sizeof(startup); PROCESS_INFORMATION child{};
        bool created = CreateProcessW(executable, command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &child) != FALSE;
        DWORD exit_code = 1;
        if (created) { WaitForSingleObject(child.hProcess, 15000); GetExitCodeProcess(child.hProcess, &exit_code); CloseHandle(child.hThread); CloseHandle(child.hProcess); }
        check(created && exit_code == 0, "Derived process inherits DNS interception");
    }
    output << (passed ? "NATIVE_DNS_PASS" : "NATIVE_DNS_FAIL") << "\n"; output.close();
    std::ofstream done(std::filesystem::path(std::wstring(argv[1]) + L".done")); done << "done";
    WSACleanup(); return passed ? 0 : 1;
}
