#include "pb_internal.h"

HANDLE windivert_handle = (HANDLE)1;
volatile BOOL running = TRUE;
volatile BOOL g_has_active_rules = TRUE;
DWORD g_current_process_id = 1;
DNS_CACHE_ENTRY *g_dns_cache[DNS_CACHE_BUCKETS] = {0};
DNS_CACHE_ENTRY_V6 *g_dns_cache_v6[DNS_CACHE_BUCKETS] = {0};
SRWLOCK g_dns_cache_lock = SRWLOCK_INIT;
static LONG sent, refused, callbacks;
static HANDLE release_queries;
static int action = RULE_ACTION_PROXY;
void log_message(const char *format, ...) { (void)format; }
DWORD get_process_id_from_udp_connection(UINT32 ip, UINT16 port) { (void)ip; (void)port; return GetCurrentProcessId(); }
DWORD get_process_id_from_udp_connection_v6(const UINT8 ip[16], UINT16 port) { (void)ip; (void)port; return GetCurrentProcessId(); }
BOOL get_process_name_from_pid(DWORD pid, char *name, DWORD size)
{ (void)pid; strcpy_s(name, size, "C:\\fixture\\application.exe"); return TRUE; }
static BOOL test_send(HANDLE handle, void *packet, UINT length, UINT *written, const WINDIVERT_ADDRESS *address)
{
    (void)handle; (void)written; (void)address;
    const BYTE *wire = (BYTE *)packet + 28;
    if (length >= 40 && (wire[2] & 0x80) && (wire[3] & 15) == 2) InterlockedIncrement(&refused);
    InterlockedIncrement(&sent); return TRUE;
}
#define WinDivertSend test_send
#include "../../native/bridge/proxy_desk.c"
#undef WinDivertSend
#include "../../native/vendor/proxybridge/Windows/src/pb_dns.c"

static int __cdecl policy(uint32_t pid, const char *path, const char *destination, uint16_t port, int udp, uint32_t *proxy)
{ (void)pid; (void)path; (void)destination; (void)port; (void)udp; *proxy = 1; return action; }
static int __cdecl resolve(uint32_t pid, const uint8_t *query, int length, uint8_t *response, int capacity)
{
    (void)pid; (void)capacity; InterlockedIncrement(&callbacks);
    WaitForSingleObject(release_queries, 5000);
    memcpy(response, query, length); response[2] |= 0x80; return length;
}
static void check(BOOL success, const char *name)
{ printf("%s: %s\n", success ? "PASS" : "FAIL", name); if (!success) exit(1); }
static void wait_for(volatile LONG *value, LONG expected)
{
    ULONGLONG deadline = GetTickCount64() + 3000;
    while (*value != expected && GetTickCount64() < deadline) Sleep(1);
    check(*value == expected, "DNS workers reached the expected lifecycle state");
}
int main(void)
{
    release_queries = CreateEventW(NULL, TRUE, FALSE, NULL);
    pd_policy_callback = policy; pd_dns_callback = resolve;
    BYTE packet[47] = {0};
    PWINDIVERT_IPHDR ip = (void *)packet; ip->Version = 4; ip->HdrLength = 5;
    ip->Length = htons(sizeof(packet)); ip->Protocol = IPPROTO_UDP; ip->TTL = 64;
    ip->SrcAddr = htonl(0xc0000201); ip->DstAddr = htonl(0xc0000235);
    PWINDIVERT_UDPHDR udp = (void *)(packet + 20); udp->SrcPort = htons(42053); udp->DstPort = htons(53); udp->Length = htons(27);
    BYTE query[] = {0x12,0x34,1,0,0,1,0,0,0,0,0,0,1,'a',0,0,1,0,1}; memcpy(packet + 28, query, sizeof(query));
    WINDIVERT_ADDRESS address = {0}; address.Outbound = TRUE;
    LARGE_INTEGER now; QueryPerformanceCounter(&now); address.Timestamp = now.QuadPart;
    pd_dns_start();
    for (int i = 0; i < 8; i++) check(pd_handle_dns_packet(packet, sizeof(packet), &address), "Proxy DNS is dispatched without waiting for the resolver");
    wait_for(&callbacks, 8);
    pd_handle_dns_packet(packet, sizeof(packet), &address);
    check(dns_jobs == 8 && sent == 1 && refused == 1, "Overload returns SERVFAIL without spawning an unbounded queue");
    action = RULE_ACTION_DIRECT;
    pd_handle_dns_packet(packet, sizeof(packet), &address);
    check(sent == 2 && callbacks == 8, "Direct DNS passes through while every proxy DNS worker is stalled");
    pd_dns_stop(); pd_dns_start(); sent = 0;
    SetEvent(release_queries); wait_for(&dns_jobs, 0);
    check(sent == 0, "Late DNS replies from the previous capture cannot enter the restarted capture");
    action = RULE_ACTION_PROXY; pd_handle_dns_packet(packet, sizeof(packet), &address); wait_for(&dns_jobs, 0);
    check(sent == 1, "New DNS requests still complete after capture restarts");
    pd_dns_stop(); CloseHandle(release_queries);
    puts("NETWORK_DNS_TEST_PASS"); return 0;
}
