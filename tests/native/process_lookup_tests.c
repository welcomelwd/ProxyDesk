#include "pb_internal.h"

SRWLOCK lock = SRWLOCK_INIT;
PID_CACHE_ENTRY *pid_cache[PID_CACHE_SIZE] = {0};
char *g_pidtbl_buf = NULL;
DWORD g_pidtbl_cap = 0;
CONNECTION_INFO *connection_hash_table[CONNECTION_HASH_SIZE] = {0};
CONNECTION_INFO *connection_rev_table[CONNECTION_HASH_SIZE] = {0};
LOGGED_CONNECTION *logged_connections = NULL;
int g_logged_count = 0;

static const UINT16 test_port = 42053;
static UINT32 test_address;
static DWORD udp_owner = 101;
static BOOL interleave = FALSE;
static HANDLE tcp_written, udp_written;

static DWORD WINAPI test_tcp_table(PVOID buffer, PDWORD size, BOOL sorted, ULONG family,
    TCP_TABLE_CLASS table_class, ULONG reserved)
{
    (void)sorted; (void)family; (void)table_class; (void)reserved;
    DWORD required = sizeof(MIB_TCPTABLE_OWNER_PID);
    if (!buffer || *size < required) { *size = required; return ERROR_INSUFFICIENT_BUFFER; }
    MIB_TCPTABLE_OWNER_PID *table = buffer;
    memset(table, 0, required);
    table->dwNumEntries = 1;
    table->table[0].dwLocalAddr = test_address;
    table->table[0].dwLocalPort = htons(test_port);
    table->table[0].dwOwningPid = 303;
    if (interleave) {
        SetEvent(tcp_written);
        WaitForSingleObject(udp_written, 100);
    }
    return NO_ERROR;
}

static DWORD WINAPI test_udp_table(PVOID buffer, PDWORD size, BOOL sorted, ULONG family,
    UDP_TABLE_CLASS table_class, ULONG reserved)
{
    (void)sorted; (void)family; (void)table_class; (void)reserved;
    DWORD required = family == AF_INET6 ? sizeof(MIB_UDP6TABLE_OWNER_PID) : sizeof(MIB_UDPTABLE_OWNER_PID);
    if (!buffer || *size < required) { *size = required; return ERROR_INSUFFICIENT_BUFFER; }
    if (family == AF_INET6) {
        MIB_UDP6TABLE_OWNER_PID *table = buffer;
        memset(table, 0, required); table->dwNumEntries = 1;
        table->table[0].ucLocalAddr[15] = 1;
        table->table[0].dwLocalPort = htons(test_port);
        table->table[0].dwOwningPid = udp_owner;
        return NO_ERROR;
    }
    MIB_UDPTABLE_OWNER_PID *table = buffer;
    memset(table, 0, required);
    table->dwNumEntries = 1;
    table->table[0].dwLocalAddr = test_address;
    table->table[0].dwLocalPort = htons(test_port);
    table->table[0].dwOwningPid = udp_owner;
    if (interleave) SetEvent(udp_written);
    return NO_ERROR;
}

DWORD sockmap_lookup_tcp(UINT16 port) { (void)port; return 0; }
void sockmap_note_scan(void) { }

#define GetExtendedTcpTable test_tcp_table
#define GetExtendedUdpTable test_udp_table
#include "../../native/vendor/proxybridge/Windows/src/pb_process.c"
#include "../../native/vendor/proxybridge/Windows/src/pb_conntrack.c"

static DWORD WINAPI lookup_tcp(void *argument)
{
    DWORD *result = argument;
    *result = get_process_id_from_connection(test_address, test_port);
    return 0;
}

static int failures = 0;
static void check(BOOL condition, const char *name)
{
    printf("%s: %s\n", condition ? "PASS" : "FAIL", name);
    if (!condition) failures++;
}

int main(void)
{
    test_address = htonl(INADDR_LOOPBACK);
    check(get_process_id_from_udp_connection(test_address, test_port) == 101,
        "UDP lookup identifies the current socket owner");
    udp_owner = 202;
    check(get_process_id_from_udp_connection(test_address, test_port) == 202,
        "Reused UDP ports do not inherit the previous application's DNS policy");
    clear_pid_cache();

    tcp_written = CreateEventW(NULL, TRUE, FALSE, NULL);
    udp_written = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (!tcp_written || !udp_written) return 2;
    interleave = TRUE;
    DWORD tcp_owner = 0;
    HANDLE thread = CreateThread(NULL, 0, lookup_tcp, &tcp_owner, 0, NULL);
    if (!thread) return 2;
    check(WaitForSingleObject(tcp_written, 3000) == WAIT_OBJECT_0,
        "Concurrent TCP lookup reaches the owner table");
    DWORD actual_udp_owner = get_process_id_from_udp_connection(test_address, test_port);
    check(WaitForSingleObject(thread, 3000) == WAIT_OBJECT_0, "Concurrent owner lookups complete");
    check(tcp_owner == 303 && actual_udp_owner == 202,
        "Concurrent TCP and DNS lookups cannot overwrite each other's owner table");
    CloseHandle(thread); CloseHandle(tcp_written); CloseHandle(udp_written);
    interleave = FALSE;
    PROCESS_IDENTITY owner;
    check(get_process_identity(GetCurrentProcessId(), &owner), "UDP cache captures a real process creation time");
    UINT32 destination = htonl(0xcb007101), changed_destination = htonl(0xcb007102);
    UINT8 source6[16] = {0}, destination6[16] = {0}; source6[15] = 1; destination6[0] = 0x20; destination6[15] = 7;
    for (int ipv6 = 0; ipv6 <= 1; ipv6++) {
        const void *source = ipv6 ? (const void *)source6 : &test_address;
        const void *target = ipv6 ? (const void *)destination6 : &destination;
        udp_owner = owner.pid;
        if (ipv6) add_connection_v6(test_port, TRUE, source6, destination6, 443, 777, &owner);
        else add_connection(test_port, TRUE, test_address, destination, 443, 777, &owner);
        udp_direct_remember(ipv6, source, target, test_port, 443, &owner);
        check(udp_flow_validate(test_port, ipv6, source, target, 443) && udp_direct_cached(ipv6, source, target, test_port, 443),
            "The current UDP owner can reuse its own proxy and direct decisions");
        udp_owner = owner.pid + 1;
        check(!udp_flow_validate(test_port, ipv6, source, target, 443) && !udp_direct_cached(ipv6, source, target, test_port, 443),
            "A new UDP owner cannot reuse either cached routing decision");
        udp_owner = owner.pid;
        PROCESS_IDENTITY old = owner; old.created--;
        if (ipv6) add_connection_v6(test_port, TRUE, source6, destination6, 443, 777, &old);
        else add_connection(test_port, TRUE, test_address, destination, 443, 777, &old);
        udp_direct_remember(ipv6, source, target, test_port, 443, &old);
        check(!udp_flow_validate(test_port, ipv6, source, target, 443) && !udp_direct_cached(ipv6, source, target, test_port, 443),
            "PID reuse cannot inherit a previous process generation's UDP decisions");
    }
    add_connection(test_port, TRUE, test_address, destination, 443, 777, &owner);
    check(!udp_flow_validate(test_port, FALSE, &test_address, &changed_destination, 8443),
        "A changed UDP destination must be evaluated against the rules again");
    UINT32 another_source = htonl(0x7f000002);
    add_connection(test_port, TRUE, test_address, destination, 443, 777, &owner);
    udp_direct_remember(FALSE, &test_address, &destination, test_port, 443, &owner);
    check(!udp_flow_validate(test_port, FALSE, &another_source, &destination, 443) && !udp_direct_cached(FALSE, &another_source, &destination, test_port, 443),
        "Different source addresses cannot share cached UDP decisions");
    udp_direct_reset();
    clear_pid_cache(); free(g_pidtbl_buf);
    if (!failures) puts("PROCESS_LOOKUP_TEST_PASS");
    return failures ? 1 : 0;
}
