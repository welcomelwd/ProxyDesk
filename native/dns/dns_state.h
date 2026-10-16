#pragma once
#ifdef PROXYDESK_DNS_TESTS
#define PD_DNS_BYPASS_EVENT L"Global\\ProxyDesk.Dns.Tests.Bypass.v1"
#define PD_DNS_DLL64 L"ProxyDeskDnsTests64.dll"
#define PD_DNS_DLL32 L"ProxyDeskDnsTests32.dll"
#else
#define PD_DNS_BYPASS_EVENT L"Global\\ProxyDesk.Dns.Bypass.v1"
#define PD_DNS_DLL64 L"ProxyDeskDns64.dll"
#define PD_DNS_DLL32 L"ProxyDeskDns32.dll"
#endif
