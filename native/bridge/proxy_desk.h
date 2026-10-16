#pragma once
#include <stdint.h>

typedef int (__cdecl *PdPolicyCallback)(uint32_t pid, const char *path, const char *destination,
    uint16_t port, int udp, uint32_t *proxy_id);
typedef int (__cdecl *PdDnsCallback)(uint32_t pid, const uint8_t *query, int query_length,
    uint8_t *response, int response_capacity);

extern PdPolicyCallback pd_policy_callback;
extern PdDnsCallback pd_dns_callback;
int pd_handle_dns_packet(void *packet, uint32_t length, void *address);
int pd_tcp_dns(uintptr_t socket_handle, uint32_t pid);

__declspec(dllexport) void __cdecl ProxyDesk_SetPolicyCallback(PdPolicyCallback callback);
__declspec(dllexport) void __cdecl ProxyDesk_SetDnsCallback(PdDnsCallback callback);
__declspec(dllexport) int __cdecl ProxyDesk_SetDnsBypass(int enabled);
