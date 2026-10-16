#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <windns.h>
#include <iphlpapi.h>
#include <tlhelp32.h>
#include <detours.h>
#include <atomic>
#include <algorithm>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>
#include "dns_state.h"
#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "dnsapi.lib")
#pragma comment(lib, "normaliz.lib")
#pragma comment(lib, "iphlpapi.lib")

static decltype(&DnsQuery_W) original_dns_w = DnsQuery_W;
static decltype(&DnsQuery_A) original_dns_a = DnsQuery_A;
static decltype(&DnsQuery_UTF8) original_dns_utf8 = DnsQuery_UTF8;
static decltype(&DnsQueryEx) original_dns_ex = DnsQueryEx;
static decltype(&DnsCancelQuery) original_dns_cancel = DnsCancelQuery;
static decltype(&getaddrinfo) original_addr_a = getaddrinfo;
static decltype(&GetAddrInfoW) original_addr_w = GetAddrInfoW;
static decltype(&GetAddrInfoExW) original_addr_ex = GetAddrInfoExW;
static decltype(&GetAddrInfoExA) original_addr_ex_a = GetAddrInfoExA;
static decltype(&GetAddrInfoExCancel) original_addr_cancel = GetAddrInfoExCancel;
static decltype(&freeaddrinfo) original_free_a = freeaddrinfo;
static decltype(&FreeAddrInfoW) original_free_w = FreeAddrInfoW;
static decltype(&FreeAddrInfoExW) original_free_ex = FreeAddrInfoExW;
static decltype(&gethostbyname) original_host = gethostbyname;
static decltype(&CreateProcessW) original_create_w = CreateProcessW;
static decltype(&CreateProcessA) original_create_a = CreateProcessA;
static thread_local bool inside_hook = false;
static std::string module_path;
static std::mutex initialize_mutex;
static bool initialized = false;
static bool dns_initialized = false;
static std::atomic<bool> dns_enabled{true};
static const GUID child_dns_mode = {0x23f4c8ac, 0xd68c, 0x4673, {0xa0, 0x97, 0xf3, 0x27, 0xe7, 0x63, 0x42, 0x91}};
static const DWORD direct_result = 0xfffffffeu;
static std::mutex memory_mutex;
static std::unordered_map<void *, std::vector<void *>> owned_results;
static std::atomic<HANDLE> bypass_event{nullptr};

static bool dns_bypassed()
{
    auto handle = bypass_event.load();
    if (!handle) {
        auto opened = OpenEventW(SYNCHRONIZE, FALSE, PD_DNS_BYPASS_EVENT);
        if (opened && !bypass_event.compare_exchange_strong(handle, opened)) CloseHandle(opened);
        handle = bypass_event.load();
    }
    return handle && WaitForSingleObject(handle, 0) == WAIT_OBJECT_0;
}

struct RecursionGuard { bool old; RecursionGuard() : old(inside_hook) { inside_hook = true; } ~RecursionGuard() { inside_hook = old; } };

static std::wstring widen(const char *value, UINT code_page = CP_UTF8)
{
    if (!value) return {};
    int size = MultiByteToWideChar(code_page, 0, value, -1, nullptr, 0);
    if (size <= 0) return {};
    std::wstring result(size, L'\0'); MultiByteToWideChar(code_page, 0, value, -1, result.data(), size);
    result.resize(size - 1); return result;
}

static std::string narrow(const wchar_t *value)
{
    int size = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
    std::string result(size, '\0'); WideCharToMultiByte(CP_UTF8, 0, value, -1, result.data(), size, nullptr, nullptr);
    result.resize(size - 1); return result;
}

static bool transfer(HANDLE pipe, BYTE *bytes, DWORD length, bool write)
{
    while (length) {
        DWORD count = 0;
        if (!(write ? WriteFile(pipe, bytes, length, &count, nullptr) : ReadFile(pipe, bytes, length, &count, nullptr)) || count == 0) return false;
        bytes += count; length -= count;
    }
    return true;
}

static int exchange(DWORD operation, const std::vector<BYTE> &query, std::vector<BYTE> &response)
{
    if ((operation == 1 || operation == 3 || operation == 4) && dns_bypassed()) return 1;
#ifdef PROXYDESK_DNS_TESTS
    constexpr auto pipe_name = L"\\\\.\\pipe\\ProxyDesk.Dns.Tests.v1";
#else
    constexpr auto pipe_name = L"\\\\.\\pipe\\ProxyDesk.Dns.v1";
#endif
    auto deadline = GetTickCount64() + 1000;
    HANDLE pipe;
    while ((pipe = CreateFileW(pipe_name, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
        SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr)) == INVALID_HANDLE_VALUE) {
        auto error = GetLastError();
        auto now = GetTickCount64();
        if (now >= deadline || (error != ERROR_PIPE_BUSY && error != ERROR_FILE_NOT_FOUND)) return -1;
        // Wait for broker availability without falling back to the system resolver.
        if (error == ERROR_FILE_NOT_FOUND) Sleep(static_cast<DWORD>((std::min)(ULONGLONG(10), deadline - now)));
        else if (!WaitNamedPipeW(pipe_name, static_cast<DWORD>(deadline - now)) && GetLastError() != ERROR_FILE_NOT_FOUND) return -1;
    }
    DWORD header[3] = {1, operation, static_cast<DWORD>(query.size())}, reply[2] = {};
    bool ok = transfer(pipe, reinterpret_cast<BYTE *>(header), sizeof(header), true) &&
        (query.empty() || transfer(pipe, const_cast<BYTE *>(query.data()), static_cast<DWORD>(query.size()), true)) &&
        transfer(pipe, reinterpret_cast<BYTE *>(reply), sizeof(reply), false);
    if (ok && reply[1] <= 65535) {
        response.resize(reply[1]);
        ok = response.empty() || transfer(pipe, response.data(), reply[1], false);
    } else ok = false;
    CloseHandle(pipe);
    return ok ? static_cast<int>(reply[0]) : -1;
}

static int register_ready()
{
    DWORD enabled = dns_enabled ? 1 : 0;
    std::vector<BYTE> query(sizeof(enabled)), response;
    memcpy(query.data(), &enabled, sizeof(enabled));
    return exchange(2, query, response);
}
static DWORD WINAPI announce_ready(void *) { register_ready(); return 0; }

static DWORD resolve_records(const wchar_t *name, WORD type, DWORD options, PDNS_RECORDW *records)
{
    *records = nullptr;
    if (!dns_enabled) return direct_result;
    if (!name || !*name) return ERROR_INVALID_PARAMETER;
    // Local literals and localhost never generate an upstream DNS query.
    IN_ADDR ip4; IN6_ADDR ip6;
    if (InetPtonW(AF_INET, name, &ip4) == 1 || InetPtonW(AF_INET6, name, &ip6) == 1 || _wcsicmp(name, L"localhost") == 0)
        return direct_result;
    if (options & DNS_QUERY_RETURN_MESSAGE) {
        std::vector<BYTE> query, response;
        return exchange(3, query, response) == 1 ? direct_result : ERROR_NOT_SUPPORTED;
    }
    wchar_t ascii[1024];
    int ascii_length = IdnToAscii(0, name, -1, ascii, 1024);
    if (ascii_length == 0) return DNS_ERROR_INVALID_NAME_CHAR;
    std::vector<BYTE> query(12, 0);
    USHORT id = static_cast<USHORT>(GetTickCount64() ^ GetCurrentThreadId());
    query[0] = static_cast<BYTE>(id >> 8); query[1] = static_cast<BYTE>(id);
    query[2] = (options & DNS_QUERY_NO_RECURSION) ? 0 : 1; query[5] = 1;
    const wchar_t *label = ascii;
    while (*label) {
        const wchar_t *end = wcschr(label, L'.'); if (!end) end = label + wcslen(label);
        size_t length = static_cast<size_t>(end - label);
        if (length == 0 || length > 63) return DNS_ERROR_INVALID_NAME;
        query.push_back(static_cast<BYTE>(length));
        for (const wchar_t *part = label; part != end; ++part) query.push_back(static_cast<BYTE>(*part));
        label = *end ? end + 1 : end;
    }
    query.push_back(0); query.push_back(static_cast<BYTE>(type >> 8)); query.push_back(static_cast<BYTE>(type));
    query.push_back(0); query.push_back(1);
    std::vector<BYTE> response;
    std::vector<BYTE> payload(sizeof(DWORD)); memcpy(payload.data(), &options, sizeof(DWORD));
    payload.insert(payload.end(), query.begin(), query.end());
    int action = exchange(4, payload, response);
    if (action == 1) return direct_result;
    if (action == 2) return DNS_ERROR_RCODE_REFUSED;
    if (action == 3) return DNS_INFO_NO_RECORDS;
    if (action != 0 || response.size() < 12) return DNS_ERROR_RCODE_SERVER_FAILURE;
    BYTE rcode = response[3] & 15;
    if (rcode) return DNS_ERROR_RCODE_FORMAT_ERROR + rcode - 1;
    auto message = reinterpret_cast<PDNS_MESSAGE_BUFFER>(response.data());
    DNS_BYTE_FLIP_HEADER_COUNTS(&message->MessageHead);
    DWORD status = DnsExtractRecordsFromMessage_W(message, static_cast<WORD>(response.size()), records);
    return status == ERROR_SUCCESS && !*records ? DNS_INFO_NO_RECORDS : status;
}

static DNS_STATUS WINAPI hook_dns_w(PCWSTR name, WORD type, DWORD options, PVOID extra, PDNS_RECORDW *records, PVOID *reserved)
{
    if (inside_hook || !dns_enabled) return original_dns_w(name, type, options, extra, records, reserved);
    RecursionGuard guard;
    auto status = resolve_records(name, type, options, records);
    return status == direct_result ? original_dns_w(name, type, options, extra, records, reserved) : status;
}

static DNS_STATUS query_narrow(PCSTR name, WORD type, DWORD options, PVOID extra, PDNS_RECORDA *records, PVOID *reserved, bool utf8)
{
    if (inside_hook || !dns_enabled) return utf8 ? original_dns_utf8(name, type, options, extra, reinterpret_cast<PDNS_RECORD *>(records), reserved) : original_dns_a(name, type, options, extra, reinterpret_cast<PDNS_RECORD *>(records), reserved);
    RecursionGuard guard; PDNS_RECORDW wide_records = nullptr;
    auto wide_name = widen(name, utf8 ? CP_UTF8 : CP_ACP);
    auto status = resolve_records(wide_name.c_str(), type, options, &wide_records);
    if (status == direct_result) return utf8 ? original_dns_utf8(name, type, options, extra, reinterpret_cast<PDNS_RECORD *>(records), reserved) : original_dns_a(name, type, options, extra, reinterpret_cast<PDNS_RECORD *>(records), reserved);
    *records = nullptr;
    if (status == ERROR_SUCCESS) {
        *records = reinterpret_cast<PDNS_RECORDA>(DnsRecordSetCopyEx(reinterpret_cast<PDNS_RECORD>(wide_records), DnsCharSetUnicode, utf8 ? DnsCharSetUtf8 : DnsCharSetAnsi));
        if (!*records) status = ERROR_NOT_ENOUGH_MEMORY;
    }
    if (wide_records) DnsRecordListFree(wide_records, DnsFreeRecordList);
    return status;
}
static DNS_STATUS WINAPI hook_dns_a(PCSTR name, WORD type, DWORD options, PVOID extra, PDNS_RECORDA *records, PVOID *reserved)
{ return query_narrow(name, type, options, extra, records, reserved, false); }
static DNS_STATUS WINAPI hook_dns_utf8(PCSTR name, WORD type, DWORD options, PVOID extra, PDNS_RECORDA *records, PVOID *reserved)
{ return query_narrow(name, type, options, extra, records, reserved, true); }

struct AsyncOperation {
    std::atomic<bool> cancelled{false}; HANDLE thread = nullptr;
    ~AsyncOperation() { if (thread) CloseHandle(thread); }
};
static std::mutex operation_mutex;
static std::unordered_map<void *, std::shared_ptr<AsyncOperation>> operations;
struct DnsOperation : AsyncOperation { DNS_QUERY_REQUEST request{}; PDNS_QUERY_RESULT result; PDNS_QUERY_CANCEL cancel; std::wstring name; };

static DWORD WINAPI dns_async_worker(void *argument)
{
    auto holder = static_cast<std::shared_ptr<DnsOperation> *>(argument);
    auto operation = *holder; delete holder;
    auto status = resolve_records(operation->name.c_str(), operation->request.QueryType, static_cast<DWORD>(operation->request.QueryOptions), &operation->result->pQueryRecords);
    if (status == direct_result) {
        auto request = operation->request; request.pQueryCompletionCallback = nullptr;
        status = original_dns_ex(&request, operation->result, nullptr);
    }
    if (operation->cancelled) {
        if (operation->result->pQueryRecords) DnsRecordListFree(operation->result->pQueryRecords, DnsFreeRecordList);
        operation->result->pQueryRecords = nullptr; status = ERROR_CANCELLED;
    }
    operation->result->QueryStatus = status;
    { std::lock_guard<std::mutex> lock(operation_mutex); operations.erase(operation->cancel); }
    operation->request.pQueryCompletionCallback(operation->request.pQueryContext, operation->result);
    return 0;
}

static DNS_STATUS WINAPI hook_dns_ex(PDNS_QUERY_REQUEST request, PDNS_QUERY_RESULT result, PDNS_QUERY_CANCEL cancel)
{
    if (inside_hook || !dns_enabled || !request || !result)
        return original_dns_ex(request, result, cancel);
    if (request->Version != DNS_QUERY_REQUEST_VERSION1) {
        std::vector<BYTE> query, response;
        return exchange(3, query, response) == 1 ? original_dns_ex(request, result, cancel) : ERROR_NOT_SUPPORTED;
    }
    if (!request->pQueryCompletionCallback) {
        RecursionGuard guard;
        auto status = resolve_records(request->QueryName, request->QueryType, static_cast<DWORD>(request->QueryOptions), &result->pQueryRecords);
        if (status == direct_result) return original_dns_ex(request, result, cancel);
        result->QueryStatus = status; result->QueryOptions = request->QueryOptions; return status;
    }
    auto operation = std::make_shared<DnsOperation>();
    operation->request = *request; operation->name = request->QueryName; operation->request.QueryName = operation->name.c_str();
    operation->result = result; operation->cancel = cancel;
    result->pQueryRecords = nullptr; result->QueryStatus = DNS_REQUEST_PENDING;
    if (cancel) ZeroMemory(cancel, sizeof(*cancel));
    auto holder = new std::shared_ptr<DnsOperation>(operation);
    operation->thread = CreateThread(nullptr, 0, dns_async_worker, holder, CREATE_SUSPENDED, nullptr);
    if (!operation->thread) { delete holder; return ERROR_NOT_ENOUGH_MEMORY; }
    if (cancel) { std::lock_guard<std::mutex> lock(operation_mutex); operations[cancel] = operation; }
    ResumeThread(operation->thread); return DNS_REQUEST_PENDING;
}

static DNS_STATUS WINAPI hook_dns_cancel(PDNS_QUERY_CANCEL cancel)
{
    std::shared_ptr<AsyncOperation> operation;
    { std::lock_guard<std::mutex> lock(operation_mutex); auto found = operations.find(cancel); if (found != operations.end()) operation = found->second; }
    if (!operation) return original_dns_cancel(cancel);
    operation->cancelled = true; CancelSynchronousIo(operation->thread); return ERROR_SUCCESS;
}

static bool free_owned(void *pointer)
{
    std::vector<void *> blocks;
    { std::lock_guard<std::mutex> lock(memory_mutex); auto found = owned_results.find(pointer); if (found == owned_results.end()) return false; blocks = std::move(found->second); owned_results.erase(found); }
    for (void *block : blocks) HeapFree(GetProcessHeap(), 0, block);
    return true;
}
static void WSAAPI hook_free_a(PADDRINFOA pointer) { if (pointer && !free_owned(pointer)) original_free_a(pointer); }
static void WSAAPI hook_free_w(PADDRINFOW pointer) { if (pointer && !free_owned(pointer)) original_free_w(pointer); }
static void WSAAPI hook_free_ex(PADDRINFOEXW pointer) { if (pointer && !free_owned(pointer)) original_free_ex(pointer); }

static bool family_configured(ULONG family)
{
    ULONG length = 16384;
    std::vector<BYTE> buffer(length);
    ULONG status = GetAdaptersAddresses(family, GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER, nullptr, reinterpret_cast<PIP_ADAPTER_ADDRESSES>(buffer.data()), &length);
    if (status == ERROR_BUFFER_OVERFLOW) { buffer.resize(length); status = GetAdaptersAddresses(family, 0, nullptr, reinterpret_cast<PIP_ADAPTER_ADDRESSES>(buffer.data()), &length); }
    if (status != NO_ERROR) return false;
    for (auto adapter = reinterpret_cast<PIP_ADAPTER_ADDRESSES>(buffer.data()); adapter; adapter = adapter->Next)
        if (adapter->OperStatus == IfOperStatusUp && adapter->IfType != IF_TYPE_SOFTWARE_LOOPBACK)
            for (auto address = adapter->FirstUnicastAddress; address; address = address->Next)
                if (address->Address.lpSockaddr->sa_family == family) return true;
    return false;
}

template<typename Node, typename Character>
static int build_addresses(const wchar_t *name, const wchar_t *service, const Node *hints, Node **output)
{
    if (!name || !*name || hints && (hints->ai_flags & AI_NUMERICHOST)) return static_cast<int>(direct_result);
    IN_ADDR ip4; IN6_ADDR ip6;
    if (InetPtonW(AF_INET, name, &ip4) == 1 || InetPtonW(AF_INET6, name, &ip6) == 1 || _wcsicmp(name, L"localhost") == 0) return static_cast<int>(direct_result);
    int family = hints ? hints->ai_family : AF_UNSPEC;
    if (family != AF_UNSPEC && family != AF_INET && family != AF_INET6) return EAI_FAMILY;
    unsigned short port = 0;
    if (service && *service) {
        wchar_t *end = nullptr; auto parsed = wcstoul(service, &end, 10);
        if (*end == 0 && parsed <= 65535) port = htons(static_cast<u_short>(parsed));
        else {
            auto text = narrow(service); auto entry = getservbyname(text.c_str(), hints && hints->ai_socktype == SOCK_DGRAM ? "udp" : "tcp");
            if (!entry) return EAI_SERVICE; port = static_cast<u_short>(entry->s_port);
        }
    }
    std::vector<void *> blocks;
    Node *first = nullptr, *last = nullptr;
    for (WORD type : {DNS_TYPE_A, DNS_TYPE_AAAA}) {
        if (family == AF_INET && type == DNS_TYPE_AAAA || family == AF_INET6 && type == DNS_TYPE_A) continue;
        int current_family = type == DNS_TYPE_A ? AF_INET : AF_INET6;
        if (hints && (hints->ai_flags & AI_ADDRCONFIG) && !family_configured(current_family)) continue;
        PDNS_RECORDW records = nullptr;
        auto status = resolve_records(name, type, DNS_QUERY_STANDARD, &records);
        if (status == direct_result) return static_cast<int>(direct_result);
        if (status != ERROR_SUCCESS && status != DNS_INFO_NO_RECORDS && status != DNS_ERROR_RCODE_NAME_ERROR) {
            for (auto block : blocks) HeapFree(GetProcessHeap(), 0, block);
            return EAI_AGAIN;
        }
        for (auto record = records; record; record = record->pNext) {
            if (record->wType != type) continue;
            auto node = static_cast<Node *>(HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(Node)));
            auto address = static_cast<sockaddr_storage *>(HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(sockaddr_storage)));
            if (!node || !address) {
                if (node) HeapFree(GetProcessHeap(), 0, node); if (address) HeapFree(GetProcessHeap(), 0, address);
                for (auto block : blocks) HeapFree(GetProcessHeap(), 0, block);
                if (records) DnsRecordListFree(records, DnsFreeRecordList);
                return EAI_MEMORY;
            }
            blocks.push_back(node); blocks.push_back(address);
            node->ai_family = current_family; node->ai_socktype = hints && hints->ai_socktype ? hints->ai_socktype : SOCK_STREAM;
            node->ai_protocol = hints && hints->ai_protocol ? hints->ai_protocol : node->ai_socktype == SOCK_DGRAM ? IPPROTO_UDP : IPPROTO_TCP;
            node->ai_addr = reinterpret_cast<sockaddr *>(address);
            if (type == DNS_TYPE_A) {
                auto v4 = reinterpret_cast<sockaddr_in *>(address); v4->sin_family = AF_INET; v4->sin_port = port; v4->sin_addr.s_addr = record->Data.A.IpAddress;
                node->ai_addrlen = sizeof(sockaddr_in);
            } else {
                auto v6 = reinterpret_cast<sockaddr_in6 *>(address); v6->sin6_family = AF_INET6; v6->sin6_port = port; memcpy(&v6->sin6_addr, &record->Data.AAAA.Ip6Address, 16);
                node->ai_addrlen = sizeof(sockaddr_in6);
            }
            if (!first) first = node; else last->ai_next = node; last = node;
        }
        if (records) DnsRecordListFree(records, DnsFreeRecordList);
    }
    if (!first) return EAI_NONAME;
    if (hints && (hints->ai_flags & AI_CANONNAME)) {
        std::basic_string<Character> canonical;
        if constexpr (sizeof(Character) == 2) canonical = name;
        else canonical = narrow(name);
        size_t bytes = (canonical.size() + 1) * sizeof(Character);
        first->ai_canonname = static_cast<Character *>(HeapAlloc(GetProcessHeap(), 0, bytes));
        if (first->ai_canonname) { memcpy(first->ai_canonname, canonical.c_str(), bytes); blocks.push_back(first->ai_canonname); }
    }
    { std::lock_guard<std::mutex> lock(memory_mutex); owned_results[first] = std::move(blocks); }
    *output = first; return 0;
}

static INT WSAAPI hook_addr_a(PCSTR name, PCSTR service, const ADDRINFOA *hints, PADDRINFOA *output)
{
    if (inside_hook || !dns_enabled || !output) return original_addr_a(name, service, hints, output);
    RecursionGuard guard; *output = nullptr;
    auto wide_name = widen(name, CP_ACP), wide_service = widen(service, CP_ACP);
    int result = build_addresses<ADDRINFOA, char>(wide_name.c_str(), service ? wide_service.c_str() : nullptr, hints, output);
    return result == static_cast<int>(direct_result) ? original_addr_a(name, service, hints, output) : result;
}
static INT WSAAPI hook_addr_w(PCWSTR name, PCWSTR service, const ADDRINFOW *hints, PADDRINFOW *output)
{
    if (inside_hook || !dns_enabled || !output) return original_addr_w(name, service, hints, output);
    RecursionGuard guard; *output = nullptr;
    int result = build_addresses<ADDRINFOW, wchar_t>(name, service, hints, output);
    return result == static_cast<int>(direct_result) ? original_addr_w(name, service, hints, output) : result;
}

struct AddressOperation : AsyncOperation {
    std::wstring name, service; ADDRINFOEXW hints{}; bool has_hints = false;
    PADDRINFOEXW *output; LPOVERLAPPED overlapped; LPLOOKUPSERVICE_COMPLETION_ROUTINE completion; HANDLE handle;
};
static DWORD WINAPI address_async_worker(void *argument)
{
    auto holder = static_cast<std::shared_ptr<AddressOperation> *>(argument); auto operation = *holder; delete holder;
    int result = build_addresses<ADDRINFOEXW, wchar_t>(operation->name.c_str(), operation->service.empty() ? nullptr : operation->service.c_str(), operation->has_hints ? &operation->hints : nullptr, operation->output);
    if (result == static_cast<int>(direct_result)) result = original_addr_ex(operation->name.c_str(), operation->service.empty() ? nullptr : operation->service.c_str(), NS_DNS, nullptr,
        operation->has_hints ? &operation->hints : nullptr, operation->output, nullptr, nullptr, nullptr, nullptr);
    if (operation->cancelled) { if (*operation->output) hook_free_ex(*operation->output); *operation->output = nullptr; result = WSA_E_CANCELLED; }
    operation->overlapped->Internal = result;
    operation->overlapped->Pointer = operation->output;
    { std::lock_guard<std::mutex> lock(operation_mutex); operations.erase(operation->handle); }
    if (operation->completion) operation->completion(result, 0, operation->overlapped);
    else if (operation->overlapped->hEvent) SetEvent(operation->overlapped->hEvent);
    return 0;
}
static INT WSAAPI hook_addr_ex(PCWSTR name, PCWSTR service, DWORD space, LPGUID provider, const ADDRINFOEXW *hints, PADDRINFOEXW *output,
    timeval *timeout, LPOVERLAPPED overlapped, LPLOOKUPSERVICE_COMPLETION_ROUTINE completion, LPHANDLE handle)
{
    if (inside_hook || !dns_enabled || !output || provider || space != NS_DNS && space != NS_ALL || !name)
        return original_addr_ex(name, service, space, provider, hints, output, timeout, overlapped, completion, handle);
    if (!overlapped) {
        RecursionGuard guard; *output = nullptr;
        int result = build_addresses<ADDRINFOEXW, wchar_t>(name, service, hints, output);
        return result == static_cast<int>(direct_result) ? original_addr_ex(name, service, space, provider, hints, output, timeout, overlapped, completion, handle) : result;
    }
    auto operation = std::make_shared<AddressOperation>();
    operation->name = name; if (service) operation->service = service;
    if (hints) { operation->hints = *hints; operation->has_hints = true; }
    operation->output = output; *output = nullptr; operation->overlapped = overlapped; operation->completion = completion;
    operation->handle = reinterpret_cast<HANDLE>(operation.get()); if (handle) *handle = operation->handle;
    overlapped->Internal = WSA_IO_PENDING;
    auto holder = new std::shared_ptr<AddressOperation>(operation);
    operation->thread = CreateThread(nullptr, 0, address_async_worker, holder, CREATE_SUSPENDED, nullptr);
    if (!operation->thread) { delete holder; return EAI_MEMORY; }
    { std::lock_guard<std::mutex> lock(operation_mutex); operations[operation->handle] = operation; }
    ResumeThread(operation->thread); return WSA_IO_PENDING;
}
static INT WSAAPI hook_addr_cancel(LPHANDLE handle)
{
    if (!handle) return WSAEINVAL;
    std::shared_ptr<AsyncOperation> operation;
    { std::lock_guard<std::mutex> lock(operation_mutex); auto found = operations.find(*handle); if (found != operations.end()) operation = found->second; }
    if (!operation) return original_addr_cancel(handle);
    operation->cancelled = true; CancelSynchronousIo(operation->thread); return 0;
}

static INT WSAAPI hook_addr_ex_a(PCSTR name, PCSTR service, DWORD space, LPGUID provider, const ADDRINFOEXA *hints,
    PADDRINFOEXA *output, timeval *timeout, LPOVERLAPPED overlapped, LPLOOKUPSERVICE_COMPLETION_ROUTINE completion, LPHANDLE handle)
{
    if (inside_hook || !dns_enabled || !output || !name || space != NS_DNS && space != NS_ALL)
        return original_addr_ex_a(name, service, space, provider, hints, output, timeout, overlapped, completion, handle);
    // The ANSI extended API does not support asynchronous operation on Windows.
    if (overlapped || completion || provider) return WSAEOPNOTSUPP;
    RecursionGuard guard; *output = nullptr;
    auto wide_name = widen(name, CP_ACP), wide_service = widen(service, CP_ACP);
    int result = build_addresses<ADDRINFOEXA, char>(wide_name.c_str(), service ? wide_service.c_str() : nullptr, hints, output);
    return result == static_cast<int>(direct_result) ? original_addr_ex_a(name, service, space, provider, hints, output, timeout, overlapped, completion, handle) : result;
}

static hostent *WSAAPI hook_host(const char *name)
{
    if (inside_hook || !dns_enabled) return original_host(name);
    ADDRINFOA hints{}; hints.ai_family = AF_INET;
    PADDRINFOA addresses = nullptr; int result = hook_addr_a(name, nullptr, &hints, &addresses);
    if (result) { WSASetLastError(WSAHOST_NOT_FOUND); return nullptr; }
    static thread_local hostent host{};
    static thread_local std::string host_name;
    static thread_local std::vector<IN_ADDR> ips;
    static thread_local std::vector<char *> pointers;
    static thread_local char *aliases[] = {nullptr};
    host_name = name; ips.clear(); pointers.clear();
    for (auto address = addresses; address; address = address->ai_next) ips.push_back(reinterpret_cast<sockaddr_in *>(address->ai_addr)->sin_addr);
    for (auto &ip : ips) pointers.push_back(reinterpret_cast<char *>(&ip)); pointers.push_back(nullptr);
    host.h_name = host_name.data(); host.h_aliases = aliases; host.h_addrtype = AF_INET; host.h_length = 4; host.h_addr_list = pointers.data();
    hook_free_a(addresses); return &host;
}

static BOOL finish_child(PROCESS_INFORMATION &child, DWORD flags, LPPROCESS_INFORMATION output)
{
    DWORD error = ERROR_SUCCESS;
    FILETIME created, exit, kernel, user;
    if (GetProcessTimes(child.hProcess, &created, &exit, &kernel, &user)) {
        std::vector<BYTE> message(12), response;
        memcpy(message.data(), &child.dwProcessId, 4); memcpy(message.data() + 4, &created, 8);
        int mode = exchange(5, message, response);
        if (mode == 0 || mode == 3) {
            DWORD enabled = mode == 0 ? 1 : 0;
            LPCSTR dll = module_path.c_str();
            if ((!DetourUpdateProcessWithDll(child.hProcess, &dll, 1) &&
                 !DetourProcessViaHelperW(child.dwProcessId, dll, original_create_w)) ||
                !DetourCopyPayloadToProcess(child.hProcess, child_dns_mode, &enabled, sizeof(enabled))) error = GetLastError();
        } else if (mode != 1) error = ERROR_PIPE_NOT_CONNECTED;
    } else error = GetLastError();
    if (error == ERROR_SUCCESS && !(flags & CREATE_SUSPENDED) && ResumeThread(child.hThread) == static_cast<DWORD>(-1)) error = GetLastError();
    if (error != ERROR_SUCCESS) {
        TerminateProcess(child.hProcess, error);
        CloseHandle(child.hThread); CloseHandle(child.hProcess);
        if (output) *output = {};
        SetLastError(error); return FALSE;
    }
    if (output) *output = child;
    else { CloseHandle(child.hThread); CloseHandle(child.hProcess); }
    return TRUE;
}

static BOOL WINAPI hook_create_w(LPCWSTR app, LPWSTR command, LPSECURITY_ATTRIBUTES process_attributes, LPSECURITY_ATTRIBUTES thread_attributes,
    BOOL inherit, DWORD flags, LPVOID environment, LPCWSTR directory, LPSTARTUPINFOW startup, LPPROCESS_INFORMATION process)
{
    if (dns_bypassed()) return original_create_w(app, command, process_attributes, thread_attributes, inherit, flags, environment, directory, startup, process);
    PROCESS_INFORMATION child{};
    if (!original_create_w(app, command, process_attributes, thread_attributes, inherit, flags | CREATE_SUSPENDED, environment, directory, startup, &child)) return FALSE;
    return finish_child(child, flags, process);
}
static BOOL WINAPI hook_create_a(LPCSTR app, LPSTR command, LPSECURITY_ATTRIBUTES process_attributes, LPSECURITY_ATTRIBUTES thread_attributes,
    BOOL inherit, DWORD flags, LPVOID environment, LPCSTR directory, LPSTARTUPINFOA startup, LPPROCESS_INFORMATION process)
{
    if (dns_bypassed()) return original_create_a(app, command, process_attributes, thread_attributes, inherit, flags, environment, directory, startup, process);
    PROCESS_INFORMATION child{};
    if (!original_create_a(app, command, process_attributes, thread_attributes, inherit, flags | CREATE_SUSPENDED, environment, directory, startup, &child)) return FALSE;
    return finish_child(child, flags, process);
}

static DWORD install_hooks(bool update_threads, bool include_dns)
{
    if (initialized && (!include_dns || dns_initialized)) return ERROR_SUCCESS;
    using ThreadHandle = std::unique_ptr<void, decltype(&CloseHandle)>;
    std::vector<ThreadHandle> threads;
    if (update_threads) {
        auto snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snapshot == INVALID_HANDLE_VALUE) return GetLastError();
        ThreadHandle snapshot_handle(snapshot, CloseHandle);
        THREADENTRY32 entry{}; entry.dwSize = sizeof(entry);
        if (!Thread32First(snapshot, &entry)) return GetLastError();
        do {
            if (entry.th32OwnerProcessID != GetCurrentProcessId() || entry.th32ThreadID == GetCurrentThreadId()) continue;
            auto thread = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_SET_CONTEXT | THREAD_QUERY_INFORMATION, FALSE, entry.th32ThreadID);
            if (!thread) { auto error = GetLastError(); if (error == ERROR_INVALID_PARAMETER) continue; return error; }
            threads.emplace_back(thread, CloseHandle);
        } while (Thread32Next(snapshot, &entry));
        if (GetLastError() != ERROR_NO_MORE_FILES) return GetLastError();
    }
    auto error = DetourTransactionBegin();
    if (error != NO_ERROR) return error;
#define ATTACH(original, hook) DetourAttach(reinterpret_cast<PVOID *>(&original), reinterpret_cast<PVOID>(hook))
    if (include_dns && !dns_initialized) {
        ATTACH(original_dns_w, hook_dns_w); ATTACH(original_dns_a, hook_dns_a); ATTACH(original_dns_utf8, hook_dns_utf8);
        ATTACH(original_dns_ex, hook_dns_ex); ATTACH(original_dns_cancel, hook_dns_cancel);
        ATTACH(original_addr_a, hook_addr_a); ATTACH(original_addr_w, hook_addr_w); ATTACH(original_addr_ex, hook_addr_ex); ATTACH(original_addr_ex_a, hook_addr_ex_a);
        ATTACH(original_addr_cancel, hook_addr_cancel); ATTACH(original_free_a, hook_free_a); ATTACH(original_free_w, hook_free_w); ATTACH(original_free_ex, hook_free_ex);
        ATTACH(original_host, hook_host);
    }
    if (!initialized) { ATTACH(original_create_w, hook_create_w); ATTACH(original_create_a, hook_create_a); }
#undef ATTACH
    for (auto &thread : threads) {
        DWORD status;
        if (GetExitCodeThread(thread.get(), &status) && status != STILL_ACTIVE) continue;
        error = DetourUpdateThread(thread.get());
        if (error != NO_ERROR) { DetourTransactionAbort(); return error; }
    }
    error = DetourTransactionCommit();
    if (error == NO_ERROR) { initialized = true; dns_initialized |= include_dns; }
    return error;
}

extern "C" DWORD WINAPI ProxyDeskInitialize(void *)
{
    dns_bypassed();
    std::lock_guard<std::mutex> lock(initialize_mutex);
    std::vector<BYTE> query, response;
    int policy = exchange(3, query, response);
    if (policy < 0) return ERROR_PIPE_NOT_CONNECTED;
    auto error = install_hooks(true, policy != 1);
    if (error != ERROR_SUCCESS) return error;
    dns_enabled = policy != 1;
    return register_ready() == 0 ? ERROR_SUCCESS : ERROR_PIPE_NOT_CONNECTED;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (DetourIsHelperProcess()) return TRUE;
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(instance);
        char path[32768]; GetModuleFileNameA(instance, path, sizeof(path)); module_path = path;
        // Launch-time loading is initialized before the application resumes. Runtime loading
        // is initialized by the helper outside the loader lock, with existing threads enlisted.
        if (!DetourRestoreAfterWith()) return TRUE;
        DWORD size = 0;
        auto mode = static_cast<DWORD *>(DetourFindPayloadEx(child_dns_mode, &size));
        if (mode && size == sizeof(DWORD)) dns_enabled = *mode != 0;
        dns_bypassed();
        if (install_hooks(false, dns_enabled) != ERROR_SUCCESS) return FALSE;
        QueueUserWorkItem(announce_ready, nullptr, WT_EXECUTEDEFAULT);
    }
    return TRUE;
}
