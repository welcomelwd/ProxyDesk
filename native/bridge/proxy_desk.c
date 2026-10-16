#include "pb_internal.h"
#include "proxy_desk.h"
#include "../dns/dns_state.h"
#include <sddl.h>
#include <aclapi.h>

PdPolicyCallback pd_policy_callback = NULL;
PdDnsCallback pd_dns_callback = NULL;
static HANDLE dns_bypass_event = NULL;

int __cdecl ProxyDesk_SetDnsBypass(int enabled)
{
    if (!dns_bypass_event) {
        PSECURITY_DESCRIPTOR descriptor = NULL;
        // Clients can observe an explicit stop; only administrators and SYSTEM can change it.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(L"O:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x00100000;;;AU)",
            SDDL_REVISION_1, &descriptor, NULL)) return FALSE;
        SECURITY_ATTRIBUTES attributes = { sizeof(attributes), descriptor, FALSE };
        dns_bypass_event = CreateEventW(&attributes, TRUE, enabled != 0, PD_DNS_BYPASS_EVENT);
        DWORD error = GetLastError(); LocalFree(descriptor); SetLastError(error);
        if (!dns_bypass_event) return FALSE;
        PSID owner = NULL; PSECURITY_DESCRIPTOR actual = NULL;
        error = GetSecurityInfo(dns_bypass_event, SE_KERNEL_OBJECT, OWNER_SECURITY_INFORMATION, &owner, NULL, NULL, NULL, &actual);
        BOOL trusted = error == ERROR_SUCCESS && (IsWellKnownSid(owner, WinBuiltinAdministratorsSid) || IsWellKnownSid(owner, WinLocalSystemSid));
        if (actual) LocalFree(actual);
        if (!trusted) { CloseHandle(dns_bypass_event); dns_bypass_event = NULL; SetLastError(error ? error : ERROR_ACCESS_DENIED); return FALSE; }
    }
    return enabled ? SetEvent(dns_bypass_event) : ResetEvent(dns_bypass_event);
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
    WINDIVERT_ADDRESS address;
} PdDnsJob;

static DWORD WINAPI dns_worker(void *state)
{
    PdDnsJob *job = state;
    BYTE response[MAXBUF];
    int size = pd_dns_callback(job->pid, job->packet + job->payload_offset,
        (int)job->payload_length, response, sizeof(response) - job->payload_offset);
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
        WinDivertSend(windivert_handle, job->packet, job->length, NULL, &job->address);
    }
    free(job);
    return 0;
}

int pd_handle_dns_packet(void *packet, uint32_t length, void *address)
{
    if (!pd_dns_callback || !pd_policy_callback) return 0;
    WINDIVERT_ADDRESS *addr = address;
    if (!addr->Outbound) return 0;
    PWINDIVERT_IPHDR ip = NULL; PWINDIVERT_IPV6HDR ip6 = NULL;
    PWINDIVERT_UDPHDR udp = NULL; void *payload = NULL; UINT payload_length = 0;
    WinDivertHelperParsePacket(packet, length, &ip, &ip6, NULL, NULL, NULL, NULL,
        &udp, &payload, &payload_length, NULL, NULL);
    if (!udp || ntohs(udp->DstPort) != 53 || payload_length < 12) return 0;
    DWORD pid = ip ? get_process_id_from_udp_connection(ip->SrcAddr, ntohs(udp->SrcPort))
        : get_process_id_from_udp_connection_v6((UINT8 *)ip6->SrcAddr, ntohs(udp->SrcPort));
    if (pid == 0 || pid == g_current_process_id) return 0;
    char path[MAX_PROCESS_NAME], destination[64];
    if (!get_process_name_from_pid(pid, path, sizeof(path))) return 0;
    if (ip) InetNtopA(AF_INET, &ip->DstAddr, destination, sizeof(destination));
    else InetNtopA(AF_INET6, ip6->DstAddr, destination, sizeof(destination));
    UINT32 proxy_id = 0;
    int action = pd_policy_callback(pid, path, destination, 53, TRUE, &proxy_id);
    if (action == RULE_ACTION_DIRECT) return 0;
    if (action == RULE_ACTION_BLOCK) return 1;
    PdDnsJob *job = malloc(sizeof(*job));
    if (!job) return 1;
    memcpy(job->packet, packet, length);
    job->length = length; job->payload_offset = (UINT)((BYTE *)payload - (BYTE *)packet);
    job->payload_length = payload_length; job->pid = pid; job->address = *addr;
    if (!QueueUserWorkItem(dns_worker, job, WT_EXECUTELONGFUNCTION)) free(job);
    return 1;
}

static int receive_exact(SOCKET socket, BYTE *data, int length)
{
    while (length > 0 && running) {
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
    DWORD timeout = 1000; setsockopt(socket, SOL_SOCKET, SO_RCVTIMEO, (char *)&timeout, sizeof(timeout));
    BYTE *query = malloc(65535), *response = malloc(65535);
    if (!query || !response) { free(query); free(response); closesocket(socket); return 0; }
    BYTE prefix[2];
    while (pd_dns_callback && receive_exact(socket, prefix, 2)) {
        int length = (prefix[0] << 8) | prefix[1];
        if (length < 12 || !receive_exact(socket, query, length)) break;
        int size = pd_dns_callback(pid, query, length, response, 65535);
        if (size <= 0) break;
        prefix[0] = (BYTE)(size >> 8); prefix[1] = (BYTE)size;
        if (!send_exact(socket, prefix, 2) || !send_exact(socket, response, size)) break;
    }
    free(query); free(response); closesocket(socket);
    return 0;
}
