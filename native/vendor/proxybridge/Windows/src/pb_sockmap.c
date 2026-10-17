#include "pb_internal.h"

// Owner-PID by local port from WinDivert's SOCKET layer.
//
// Finding the process behind a new outbound TCP connection used to mean fetching the whole
// system TCP table (GetExtendedTcpTable) for every SYN, on the single packet-capture thread.
// The table includes every TIME_WAIT socket, so on a machine that has been busy for hours it
// has tens of thousands of rows and one fetch costs several milliseconds - a few hundred new
// connections per second saturate the thread and everything captured slows down (measured:
// ~3.6 ms per fetch at ~9.6k rows, ~20 ms of capture-thread time per proxied connection).
//
// The socket layer reports every connect() together with its process ID *before* the SYN is
// sent, so a small port-indexed table filled from those events answers the lookup in O(1).
// Anything it cannot answer (event not seen yet, entry too old, socket-layer open failed)
// falls back to the table scan, so behaviour is never worse than before.

#define SOCKMAP_FRESH_MS 8000   // a SYN is evaluated within ~2 s even when the capture queue is backed up

typedef struct { DWORD pid, tick; UINT64 endpoint; UINT32 local_addr[4]; } SOCKMAP_ENTRY;
static SOCKMAP_ENTRY g_sockmap[2][65536];
static SRWLOCK g_sockmap_lock = SRWLOCK_INIT;
static HANDLE g_sock_handle = INVALID_HANDLE_VALUE;
static HANDLE g_sock_thread = NULL;
static volatile LONG g_sock_events = 0, g_sock_hits = 0, g_sock_scans = 0;

static DWORD WINAPI sockmap_thread(LPVOID arg)
{
    (void)arg;
    WINDIVERT_ADDRESS addr;
    while (1)
    {
        if (!WinDivertRecv(g_sock_handle, NULL, 0, NULL, &addr))
        {
            DWORD err = GetLastError();
            if (!running || err == ERROR_INVALID_HANDLE || err == ERROR_NO_DATA || err == ERROR_OPERATION_ABORTED)
                break;
            Sleep(5);
            continue;
        }
        if ((addr.Event == WINDIVERT_EVENT_SOCKET_CONNECT || addr.Event == WINDIVERT_EVENT_SOCKET_CLOSE) && addr.Socket.Protocol == IPPROTO_TCP &&
            addr.Socket.LocalPort != 0 && addr.Socket.ProcessId != 0)
        {
            AcquireSRWLockExclusive(&g_sockmap_lock);
            SOCKMAP_ENTRY *e = &g_sockmap[addr.IPv6 != 0][addr.Socket.LocalPort];
            if (addr.Event == WINDIVERT_EVENT_SOCKET_CLOSE)
            {
                // A late close from an older socket must not invalidate its replacement.
                if (e->endpoint == addr.Socket.EndpointId) e->pid = 0;
                ReleaseSRWLockExclusive(&g_sockmap_lock);
                continue;
            }
            e->pid = addr.Socket.ProcessId; e->tick = GetTickCount();
            e->endpoint = addr.Socket.EndpointId;
            memcpy(e->local_addr, addr.Socket.LocalAddr, sizeof(e->local_addr));
            ReleaseSRWLockExclusive(&g_sockmap_lock);
            if (InterlockedIncrement(&g_sock_events) == 1)
                log_message("Process lookup: socket events active (first: PID %lu, local port %u)",
                            (unsigned long)addr.Socket.ProcessId, (unsigned)addr.Socket.LocalPort);
        }
    }
    return 0;
}

BOOL sockmap_start(void)
{
    ZeroMemory(g_sockmap, sizeof(g_sockmap));
    g_sock_events = g_sock_hits = g_sock_scans = 0;
    g_sock_handle = WinDivertOpen("tcp and (event == CONNECT or event == CLOSE)", WINDIVERT_LAYER_SOCKET, 0,
                                  WINDIVERT_FLAG_SNIFF | WINDIVERT_FLAG_RECV_ONLY);
    if (g_sock_handle == INVALID_HANDLE_VALUE)
    {
        log_message("Process lookup: socket layer unavailable (%lu), using the TCP table scan", GetLastError());
        return FALSE;
    }
    WinDivertSetParam(g_sock_handle, WINDIVERT_PARAM_QUEUE_LENGTH, 16384);
    g_sock_thread = CreateThread(NULL, 0, sockmap_thread, NULL, 0, NULL);
    if (g_sock_thread == NULL)
    {
        WinDivertClose(g_sock_handle);
        g_sock_handle = INVALID_HANDLE_VALUE;
        return FALSE;
    }
    return TRUE;
}

void sockmap_stop(void)
{
    if (g_sock_handle != INVALID_HANDLE_VALUE)
    {
        WinDivertShutdown(g_sock_handle, WINDIVERT_SHUTDOWN_BOTH);
        WinDivertClose(g_sock_handle);
        g_sock_handle = INVALID_HANDLE_VALUE;
    }
    if (g_sock_thread != NULL)
    {
        WaitForSingleObject(g_sock_thread, 2000);
        CloseHandle(g_sock_thread);
        g_sock_thread = NULL;
        log_message("Process lookup: %ld connections resolved via socket events, %ld via table scan",
                    g_sock_hits, g_sock_scans);
    }
}

// Match the address family and local address as well as the port. Ports alone are not identities.
DWORD sockmap_lookup_tcp(BOOL ipv6, const void *src_ip, UINT16 port)
{
    if (g_sock_thread == NULL || port == 0)
        return 0;
    UINT32 address[4] = {0};
    if (ipv6)
    {
        UINT32 network[4]; memcpy(network, src_ip, sizeof(network));
        for (int i = 0; i < 4; i++) address[i] = ntohl(network[3 - i]);
    }
    else { address[0] = ntohl(*(const UINT32 *)src_ip); address[1] = 0xffff; }
    AcquireSRWLockShared(&g_sockmap_lock);
    SOCKMAP_ENTRY entry = g_sockmap[ipv6 != 0][port];
    ReleaseSRWLockShared(&g_sockmap_lock);
    if (!entry.pid || (DWORD)(GetTickCount() - entry.tick) > SOCKMAP_FRESH_MS ||
        memcmp(entry.local_addr, address, sizeof(address)) != 0)
        return 0;
    InterlockedIncrement(&g_sock_hits);
    return entry.pid;
}

void sockmap_note_scan(void)
{
    InterlockedIncrement(&g_sock_scans);
}
