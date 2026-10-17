#include "pb_internal.h"

SRWLOCK lock = SRWLOCK_INIT;
PID_CACHE_ENTRY *pid_cache[PID_CACHE_SIZE] = {0};
char *g_pidtbl_buf = NULL;
DWORD g_pidtbl_cap = 0;
CONNECTION_INFO *connection_hash_table[CONNECTION_HASH_SIZE] = {0};
CONNECTION_INFO *connection_rev_table[CONNECTION_HASH_SIZE] = {0};
LOGGED_CONNECTION *logged_connections = NULL;
int g_logged_count = 0;
volatile BOOL running = FALSE;
volatile LONG port_decided_bitmap[2][2048] = {0};
volatile LONG port_direct_bitmap[2][2048] = {0};

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

static WINDIVERT_ADDRESS socket_event;
static BOOL socket_event_pending;
static BOOL test_socket_receive(HANDLE handle, void *packet, UINT capacity, UINT *length, WINDIVERT_ADDRESS *address)
{
    (void)handle; (void)packet; (void)capacity; (void)length;
    if (!socket_event_pending) { SetLastError(ERROR_NO_DATA); return FALSE; }
    *address = socket_event; socket_event_pending = FALSE; return TRUE;
}
void log_message(const char *format, ...) { (void)format; }
static HANDLE test_socket_open(const char *filter, WINDIVERT_LAYER layer, INT16 priority, UINT64 flags)
{ (void)filter; (void)layer; (void)priority; (void)flags; abort(); return INVALID_HANDLE_VALUE; }
static BOOL test_socket_shutdown(HANDLE handle, WINDIVERT_SHUTDOWN how)
{ (void)handle; (void)how; abort(); return FALSE; }
static BOOL test_socket_close(HANDLE handle)
{ (void)handle; abort(); return FALSE; }
static BOOL test_socket_param(HANDLE handle, WINDIVERT_PARAM param, UINT64 value)
{ (void)handle; (void)param; (void)value; abort(); return FALSE; }
#define WinDivertRecv test_socket_receive
#define WinDivertOpen test_socket_open
#define WinDivertShutdown test_socket_shutdown
#define WinDivertClose test_socket_close
#define WinDivertSetParam test_socket_param
#include "../../native/vendor/proxybridge/Windows/src/pb_sockmap.c"
#undef WinDivertRecv
#undef WinDivertOpen
#undef WinDivertShutdown
#undef WinDivertClose
#undef WinDivertSetParam

static void record_socket_event(BOOL ipv6, UINT32 address, DWORD pid, UINT64 endpoint, WINDIVERT_EVENT event)
{
    memset(&socket_event, 0, sizeof(socket_event));
    socket_event.Event = event; socket_event.IPv6 = ipv6;
    socket_event.Socket.Protocol = IPPROTO_TCP;
    socket_event.Socket.LocalPort = test_port; socket_event.Socket.ProcessId = pid;
    socket_event.Socket.EndpointId = endpoint;
    socket_event.Socket.LocalAddr[0] = ntohl(address);
    socket_event.Socket.LocalAddr[1] = ipv6 ? 0 : 0xffff;
    socket_event_pending = TRUE;
    sockmap_thread(NULL);
}

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
    g_sock_thread = (HANDLE)1;
    clear_pid_cache();
    record_socket_event(TRUE, test_address, 999, 1, WINDIVERT_EVENT_SOCKET_CONNECT);
    check(get_process_id_from_connection(test_address, test_port) == 303,
        "An IPv6 proxy connection cannot take ownership of an IPv4 direct connection on the same port");
    clear_pid_cache();
    record_socket_event(FALSE, htonl(0x7f000002), 999, 2, WINDIVERT_EVENT_SOCKET_CONNECT);
    check(get_process_id_from_connection(test_address, test_port) == 303,
        "A different local address cannot take ownership of a direct connection on the same port");
    clear_pid_cache();
    record_socket_event(FALSE, test_address, 999, 3, WINDIVERT_EVENT_SOCKET_CONNECT);
    check(get_process_id_from_connection(test_address, test_port) == 999,
        "A matching socket event still resolves its current owner");
    clear_pid_cache();
    record_socket_event(FALSE, test_address, 999, 3, WINDIVERT_EVENT_SOCKET_CLOSE);
    check(get_process_id_from_connection(test_address, test_port) == 303,
        "A closed socket cannot supply the previous owner's identity for a reused port");
    clear_pid_cache();
    record_socket_event(FALSE, test_address, 999, 4, WINDIVERT_EVENT_SOCKET_CONNECT);
    record_socket_event(FALSE, test_address, 999, 3, WINDIVERT_EVENT_SOCKET_CLOSE);
    check(get_process_id_from_connection(test_address, test_port) == 999,
        "A delayed close does not erase the replacement socket's owner");
    UINT8 tcp_source6[16]; InetPtonA(AF_INET6, "2001:db8::1234", tcp_source6);
    socket_event.IPv6 = TRUE; socket_event.Event = WINDIVERT_EVENT_SOCKET_CONNECT;
    socket_event.Socket.LocalAddr[0] = 0x1234; socket_event.Socket.LocalAddr[1] = 0;
    socket_event.Socket.LocalAddr[2] = 0; socket_event.Socket.LocalAddr[3] = 0x20010db8;
    socket_event_pending = TRUE; sockmap_thread(NULL);
    check(get_process_id_from_connection_v6(tcp_source6, test_port) == 999,
        "IPv6 socket event addresses match packet addresses in network byte order");
    tcp_source6[15]++;
    check(sockmap_lookup_tcp(TRUE, tcp_source6, test_port) == 0,
        "A different IPv6 local address cannot reuse the cached socket owner");
    g_sock_thread = NULL;
    for (int ipv6 = 0; ipv6 <= 1; ipv6++) {
        port_clear(test_port, FALSE); port_clear(test_port, TRUE);
        port_set_direct(test_port, ipv6);
        port_set_decided(test_port, !ipv6);
        check(port_is_direct(test_port, ipv6) && !port_is_direct(test_port, !ipv6),
            "Direct and proxy TCP decisions on the same port stay isolated by address family");
        port_clear(test_port, !ipv6);
        check(port_is_decided(test_port, ipv6) && port_is_direct(test_port, ipv6),
            "Closing the other address family preserves an active direct TCP connection");
    }
    PROCESS_IDENTITY owner;
    check(get_process_identity(GetCurrentProcessId(), &owner), "UDP cache captures a real process creation time");
    add_connection(test_port, FALSE, test_address, htonl(0xc0000235), 53, 777, &owner);
    check(get_tracked_tcp_pid(test_port, FALSE) == owner.pid,
        "TCP DNS retains the original owner after packet redirection changes the peer address");
    PROCESS_IDENTITY stale_tcp = owner; stale_tcp.created--;
    add_connection(test_port, FALSE, test_address, htonl(0xc0000235), 53, 777, &stale_tcp);
    check(get_tracked_tcp_pid(test_port, FALSE) == 0, "TCP DNS rejects a recycled process identity");
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
