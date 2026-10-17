#include "pb_internal.h"
#include "proxy_desk.h"
#include <sddl.h>
#include <aclapi.h>

PdPolicyCallback pd_policy_callback = NULL;
PdDnsCallback pd_dns_callback = NULL;
static HANDLE dns_bypass_event = NULL;
static SRWLOCK dns_send_lock = SRWLOCK_INIT;
static volatile LONG dns_epoch = 0;
static BOOL dns_active = FALSE;
static volatile LONG dns_jobs = 0;

void pd_dns_start(void)
{
    AcquireSRWLockExclusive(&dns_send_lock);
    InterlockedIncrement(&dns_epoch); dns_active = TRUE;
    ReleaseSRWLockExclusive(&dns_send_lock);
}

void pd_dns_stop(void)
{
    AcquireSRWLockExclusive(&dns_send_lock);
    dns_active = FALSE; InterlockedIncrement(&dns_epoch);
    ReleaseSRWLockExclusive(&dns_send_lock);
}

static void trace_dns_packet(const char *stage, const void *packet, UINT length,
    const WINDIVERT_ADDRESS *address, DWORD pid, const char *path, DWORD error)
{
    PWINDIVERT_IPHDR ip = NULL; PWINDIVERT_IPV6HDR ip6 = NULL;
    PWINDIVERT_UDPHDR udp = NULL; void *payload = NULL; UINT size = 0;
    WinDivertHelperParsePacket(packet, length, &ip, &ip6, NULL, NULL, NULL, NULL,
        &udp, &payload, &size, NULL, NULL);
    if (!udp || size < 12) return;
    char local[64], remote[64], domain[256] = "<invalid>";
    InetNtopA(ip ? AF_INET : AF_INET6, ip ? (void *)&ip->SrcAddr : ip6->SrcAddr, local, sizeof(local));
    InetNtopA(ip ? AF_INET : AF_INET6, ip ? (void *)&ip->DstAddr : ip6->DstAddr, remote, sizeof(remote));
    const BYTE *wire = payload; int offset = 12;
    BOOL parsed = dns_parse_name(wire, size, &offset, domain, sizeof(domain));
    for (char *p = domain; *p; p++) if ((unsigned char)*p < 32 || *p == '"') *p = '?';
    UINT type = parsed && offset + 4 <= (int)size ? ((UINT)wire[offset] << 8) | wire[offset + 1] : 0;
    LARGE_INTEGER now, frequency; QueryPerformanceCounter(&now); QueryPerformanceFrequency(&frequency);
    LONGLONG delay = (now.QuadPart - address->Timestamp) * 1000 / frequency.QuadPart;
    log_message("DNS packet %s: source=udp-capture pid=%lu path=\"%.260s\" from=[%s]:%u to=[%s]:%u domain=\"%s\" qtype=%u queryId=%u response=%u rcode=%u captureDelayMs=%lld win32Error=%lu",
        stage, pid, path ? path : "<unresolved>", local, ntohs(udp->SrcPort), remote, ntohs(udp->DstPort),
        domain, type, ((UINT)wire[0] << 8) | wire[1], (wire[2] & 0x80) != 0, wire[3] & 15, delay, error);
}

int __cdecl ProxyDesk_DisableLegacyDns(void)
{
    if (!dns_bypass_event) {
        PSECURITY_DESCRIPTOR descriptor = NULL;
        // Keep already-loaded legacy hooks bypassed until their host applications exit.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(L"O:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x00100000;;;AU)",
            SDDL_REVISION_1, &descriptor, NULL)) return FALSE;
        SECURITY_ATTRIBUTES attributes = { sizeof(attributes), descriptor, FALSE };
        dns_bypass_event = CreateEventW(&attributes, TRUE, TRUE, L"Global\\ProxyDesk.Dns.Bypass.v1");
        DWORD error = GetLastError(); LocalFree(descriptor); SetLastError(error);
        if (!dns_bypass_event) return FALSE;
        PSID owner = NULL; PSECURITY_DESCRIPTOR actual = NULL;
        error = GetSecurityInfo(dns_bypass_event, SE_KERNEL_OBJECT, OWNER_SECURITY_INFORMATION, &owner, NULL, NULL, NULL, &actual);
        BOOL trusted = error == ERROR_SUCCESS && (IsWellKnownSid(owner, WinBuiltinAdministratorsSid) || IsWellKnownSid(owner, WinLocalSystemSid));
        if (actual) LocalFree(actual);
        if (!trusted) { CloseHandle(dns_bypass_event); dns_bypass_event = NULL; SetLastError(error ? error : ERROR_ACCESS_DENIED); return FALSE; }
    }
    return SetEvent(dns_bypass_event);
}

void __cdecl ProxyDesk_SetPolicyCallback(PdPolicyCallback callback)
{
    pd_policy_callback = callback;
    g_has_active_rules = callback != NULL;
}

void __cdecl ProxyDesk_SetDnsCallback(PdDnsCallback callback) { pd_dns_callback = callback; }

typedef struct {
    BYTE packet[MAXBUF];
    UINT length;
    UINT payload_offset;
    UINT payload_length;
    DWORD pid;
    LONG epoch;
    WINDIVERT_ADDRESS address;
} PdDnsJob;

static void dns_reply(PdDnsJob *job, BYTE *response, int size)
{
    AcquireSRWLockShared(&dns_send_lock);
    if (!dns_active || job->epoch != dns_epoch) { ReleaseSRWLockShared(&dns_send_lock); return; }
    if (size > 0) {
        PWINDIVERT_IPHDR ip = NULL; PWINDIVERT_IPV6HDR ip6 = NULL;
        PWINDIVERT_UDPHDR udp = NULL;
        WinDivertHelperParsePacket(job->packet, job->length, &ip, &ip6,
            NULL, NULL, NULL, NULL, &udp, NULL, NULL, NULL, NULL);
        // A truncated response makes ordinary UDP clients retry over TCP.
        if (size > 512) {
            size = (int)job->payload_length;
            memcpy(response, job->packet + job->payload_offset, size);
            response[2] = (BYTE)((response[2] | 0x82) & ~0x04);
            response[3] |= 0x80;
            memset(response + 6, 0, 6);
        }
        memcpy(job->packet + job->payload_offset, response, size);
        job->length = job->payload_offset + size;
        UINT16 port = udp->SrcPort; udp->SrcPort = udp->DstPort; udp->DstPort = port;
        udp->Length = htons((UINT16)(size + 8));
        if (ip) {
            UINT32 address = ip->SrcAddr; ip->SrcAddr = ip->DstAddr; ip->DstAddr = address;
            ip->Length = htons((UINT16)job->length);
        } else {
            BYTE address[16]; memcpy(address, ip6->SrcAddr, 16);
            memcpy(ip6->SrcAddr, ip6->DstAddr, 16); memcpy(ip6->DstAddr, address, 16);
            ip6->Length = htons((UINT16)(job->length - 40));
        }
        job->address.Outbound = job->address.Loopback ? TRUE : FALSE;
        WinDivertHelperCalcChecksums(job->packet, job->length, &job->address, 0);
        if (!WinDivertSend(windivert_handle, job->packet, job->length, NULL, &job->address))
            trace_dns_packet("failed-reply", job->packet, job->length, &job->address, job->pid, NULL, GetLastError());
    }
    else if (size == -1) {
        if (!WinDivertSend(windivert_handle, job->packet, job->length, NULL, &job->address))
            trace_dns_packet("failed-passthrough", job->packet, job->length, &job->address, job->pid, NULL, GetLastError());
    }
    else trace_dns_packet("failed-resolver", job->packet, job->length, &job->address, job->pid, NULL, 0);
    ReleaseSRWLockShared(&dns_send_lock);
}

static void dns_overload(PdDnsJob *job, DWORD error)
{
    trace_dns_packet("failed-capacity", job->packet, job->length, &job->address, job->pid, NULL, error);
    BYTE *response = job->packet + job->payload_offset;
    response[2] |= 0x80; response[3] = (BYTE)((response[3] & 0xf0) | 2);
    memset(response + 6, 0, 6);
    dns_reply(job, response, job->payload_length);
}

static DWORD WINAPI dns_worker(void *state)
{
    PdDnsJob *job = state;
    BYTE response[MAXBUF];
    if (job->epoch == dns_epoch) {
        int size = pd_dns_callback(job->pid, job->packet + job->payload_offset,
            (int)job->payload_length, response, sizeof(response) - job->payload_offset);
        dns_reply(job, response, size);
    }
    free(job);
    InterlockedDecrement(&dns_jobs);
    return 0;
}

static int pass_dns_packet(void *packet, uint32_t length, WINDIVERT_ADDRESS *address, DWORD pid, const char *path)
{
    if (!WinDivertSend(windivert_handle, packet, length, NULL, address))
        trace_dns_packet("failed-passthrough", packet, length, address, pid, path, GetLastError());
    else trace_dns_packet("direct-sent", packet, length, address, pid, path, 0);
    return 1;
}

int pd_handle_dns_packet(void *packet, uint32_t length, void *address)
{
    if (!pd_dns_callback || !pd_policy_callback) return 0;
    WINDIVERT_ADDRESS *addr = address;
    PWINDIVERT_IPHDR ip = NULL; PWINDIVERT_IPV6HDR ip6 = NULL;
    PWINDIVERT_UDPHDR udp = NULL; void *payload = NULL; UINT payload_length = 0;
    WinDivertHelperParsePacket(packet, length, &ip, &ip6, NULL, NULL, NULL, NULL,
        &udp, &payload, &payload_length, NULL, NULL);
    if (!udp || payload_length < 12) return 0;
    if (ntohs(udp->SrcPort) == 53 && (((BYTE *)payload)[2] & 0x80)) {
        trace_dns_packet("response-observed", packet, length, addr, 0, NULL, 0);
        return 0;
    }
    if (!addr->Outbound || ntohs(udp->DstPort) != 53) return 0;
    DWORD pid = ip ? get_process_id_from_udp_connection(ip->SrcAddr, ntohs(udp->SrcPort))
        : get_process_id_from_udp_connection_v6((UINT8 *)ip6->SrcAddr, ntohs(udp->SrcPort));
    if (pid == 0 || pid == g_current_process_id) return pass_dns_packet(packet, length, addr, pid, NULL);
    char path[MAX_PROCESS_NAME], destination[64];
    if (!get_process_name_from_pid(pid, path, sizeof(path))) return pass_dns_packet(packet, length, addr, pid, NULL);
    if (ip) InetNtopA(AF_INET, &ip->DstAddr, destination, sizeof(destination));
    else InetNtopA(AF_INET6, ip6->DstAddr, destination, sizeof(destination));
    UINT32 proxy_id = 0;
    int action = pd_policy_callback(pid, path, destination, 53, TRUE, &proxy_id);
    // A direct DNS decision is final; generic UDP flow state must not reroute it.
    if (action == RULE_ACTION_DIRECT) return pass_dns_packet(packet, length, addr, pid, path);
    trace_dns_packet(action == RULE_ACTION_BLOCK ? "blocked" : "proxy-selected", packet, length, addr, pid, path, 0);
    if (action == RULE_ACTION_BLOCK) return 1;
    PdDnsJob *job = malloc(sizeof(*job));
    if (!job) { trace_dns_packet("failed-allocation", packet, length, addr, pid, path, ERROR_NOT_ENOUGH_MEMORY); return 1; }
    memcpy(job->packet, packet, length);
    job->length = length; job->payload_offset = (UINT)((BYTE *)payload - (BYTE *)packet);
    job->payload_length = payload_length; job->pid = pid; job->address = *addr; job->epoch = dns_epoch;
    if (InterlockedIncrement(&dns_jobs) > 8) {
        InterlockedDecrement(&dns_jobs); dns_overload(job, ERROR_BUSY); free(job); return 1;
    }
    // Bounded dedicated workers keep upstream waits off both packet and shared worker threads.
    HANDLE thread = CreateThread(NULL, 0, dns_worker, job, 0, NULL);
    if (!thread) { DWORD error = GetLastError(); InterlockedDecrement(&dns_jobs); dns_overload(job, error); free(job); }
    else CloseHandle(thread);
    return 1;
}

static int receive_exact(SOCKET socket, BYTE *data, int length, LONG epoch)
{
    while (length > 0 && running && epoch == dns_epoch) {
        int count = recv(socket, (char *)data, length, 0);
        if (count == SOCKET_ERROR && WSAGetLastError() == WSAETIMEDOUT) continue;
        if (count <= 0) return 0; data += count; length -= count;
    }
    return length == 0;
}

static int send_exact(SOCKET socket, const BYTE *data, int length)
{
    while (length > 0) { int count = send(socket, (const char *)data, length, 0); if (count <= 0) return 0; data += count; length -= count; }
    return 1;
}

int pd_tcp_dns(uintptr_t socket_handle, uint32_t pid)
{
    SOCKET socket = (SOCKET)socket_handle;
    LONG epoch = dns_epoch;
    DWORD timeout = 1000; setsockopt(socket, SOL_SOCKET, SO_RCVTIMEO, (char *)&timeout, sizeof(timeout));
    setsockopt(socket, SOL_SOCKET, SO_SNDTIMEO, (char *)&timeout, sizeof(timeout));
    BYTE *query = malloc(65535), *response = malloc(65535);
    if (!query || !response) { free(query); free(response); closesocket(socket); return 0; }
    BYTE prefix[2];
    while (pd_dns_callback && receive_exact(socket, prefix, 2, epoch)) {
        int length = (prefix[0] << 8) | prefix[1];
        if (length < 12 || !receive_exact(socket, query, length, epoch)) break;
        int size = pd_dns_callback(pid, query, length, response, 65535);
        if (size <= 0 || epoch != dns_epoch) break;
        prefix[0] = (BYTE)(size >> 8); prefix[1] = (BYTE)size;
        if (!send_exact(socket, prefix, 2) || !send_exact(socket, response, size)) break;
    }
    free(query); free(response); closesocket(socket);
    return 0;
}
