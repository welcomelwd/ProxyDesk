using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ProxyDesk.Core;

namespace ProxyDesk.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsTrafficBackend : ITrafficBackend
{
    private readonly RuleEngine rules;
    private readonly Func<int, byte[], byte[]> resolveDns;
    private readonly Func<int, string, IReadOnlyList<ApplicationProcess>> getLineage;
    private readonly PolicyCallback policyCallback;
    private readonly DnsCallback dnsCallback;
    private readonly LogCallback logCallback;
    private readonly Dictionary<string, uint> nativeGenerations = [];
    private readonly ConcurrentDictionary<(Guid Id, Guid Revision), uint> currentProxyIds = new();
    private readonly List<HttpsBridge> bridges = [];
    private bool loaded;
    private bool running;
    private string? nativeError;
    private string dependencyDescription = "WFP / WinDivert; application DNS is attached automatically.";
    private nint dependencyHandle;
    private long sequence;
    public event Action<ConnectionEvent>? Connection;
    public event Action<string>? Log;

    public WindowsTrafficBackend(RuleEngine rules, Func<int, byte[], byte[]> resolveDns, Func<int, string, IReadOnlyList<ApplicationProcess>> getLineage)
    {
        this.rules = rules; this.resolveDns = resolveDns; this.getLineage = getLineage;
        policyCallback = Decide; dnsCallback = Resolve; logCallback = message => Log?.Invoke(message);
        var core = Path.Combine(AppContext.BaseDirectory, "ProxyDeskCore.dll");
        if (!File.Exists(core)) { nativeError = "ProxyDeskCore.dll is missing. Build the native backend first."; return; }
        try
        {
            string? directory = File.Exists(Path.Combine(AppContext.BaseDirectory, "WinDivert64.sys")) && File.Exists(Path.Combine(AppContext.BaseDirectory, "WinDivert.dll"))
                ? AppContext.BaseDirectory : InstalledWinDivert.FindDirectory();
            if (directory is null)
            {
                nativeError = "The official signed WinDivert driver is missing or was blocked by system security software."; return;
            }
            WinDivertPackage.VerifySignature(Path.Combine(directory, "WinDivert64.sys"));
            dependencyHandle = LoadLibraryEx(Path.Combine(directory, "WinDivert.dll"), 0, 0x800);
            if (dependencyHandle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            SetPolicyCallback(policyCallback); SetDnsCallback(dnsCallback); SetLogCallback(logCallback);
            loaded = true;
            dependencyDescription = directory == AppContext.BaseDirectory ? "内置 WinDivert 组件已校验并加载。" :
                "已校验并加载兼容 WinDivert：" + directory;
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { nativeError = error.Message; }
    }

    public BackendCapabilities Capabilities => new(loaded,
        File.Exists(Path.Combine(AppContext.BaseDirectory, "ProxyDeskDns32.dll")) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "ProxyDeskDns64.dll")) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "ProxyDesk.Injector32.exe")) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "ProxyDesk.Injector64.exe")), true, "Windows",
        nativeError ?? dependencyDescription);

    public void Apply(AppConfiguration configuration, Func<Guid, string> getPassword)
    {
        if (!loaded) return;
        foreach (var proxy in configuration.Proxies)
        {
            var password = getPassword(proxy.Id);
            var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { proxy.Kind, proxy.Host, proxy.Port, proxy.Username, Password = password }))));
            if (!nativeGenerations.TryGetValue(fingerprint, out var id))
            {
                if (nativeGenerations.Count >= 256) throw new InvalidOperationException("Proxy generation limit reached. Restart the service to release old generations.");
                if (proxy.Kind == ProxyKind.Https)
                {
                    var bridge = new HttpsBridge(proxy, password, message => Log?.Invoke(message)); bridges.Add(bridge);
                    id = AddProxyConfig(0, "127.0.0.1", (ushort)bridge.Port, "", "", false);
                }
                else id = AddProxyConfig(proxy.Kind == ProxyKind.Socks5 ? 1 : 0, proxy.Host, (ushort)proxy.Port,
                    proxy.Username, password, false);
                if (id == 0) throw new InvalidOperationException("The native backend rejected a proxy configuration.");
                nativeGenerations.Add(fingerprint, id);
            }
            currentProxyIds[(proxy.Id, proxy.Revision)] = id;
        }
    }

    private int Decide(uint pid, string path, string destination, ushort port, int udp, out uint proxyId)
    {
        proxyId = 0;
        try
        {
            var snapshot = rules.Configuration;
            if (pid == Environment.ProcessId || WindowsIntegration.FindLocalProxyProcesses(snapshot.Proxies).Contains((int)pid)) return 1;
            var decision = RuleEngine.Evaluate(snapshot, getLineage((int)pid, path), AppPlatform.Windows);
            var action = decision.Action;
            var detail = "";
            if (port == 53 && action == RoutingAction.Proxy && !decision.StrictDns) { action = RoutingAction.Direct; detail = "The rule uses system DNS."; }
            if (action == RoutingAction.Proxy)
            {
                var proxy = snapshot.Proxies.Single(p => p.Id == decision.ProxyId);
                if (udp != 0 && port != 53 && proxy.Kind != ProxyKind.Socks5)
                { action = RoutingAction.Block; detail = "UDP requires a SOCKS5 proxy with UDP ASSOCIATE support."; }
                else if (!proxy.AllowPrivateNetworks && IPAddress.TryParse(destination, out var ip) && IsPrivate(ip) && port != 53)
                { action = RoutingAction.Direct; detail = "Private network access is configured as direct."; }
                else if (!currentProxyIds.TryGetValue((proxy.Id, proxy.Revision), out proxyId))
                { action = RoutingAction.Block; detail = "The selected proxy is unavailable."; }
            }
            Connection?.Invoke(new(Interlocked.Increment(ref sequence), DateTimeOffset.Now,
                port == 53 ? "DNS" : udp != 0 ? "UDP" : "TCP", path, (int)pid, destination, port,
                action, decision.ProxyId, decision.RuleId, detail));
            return action switch { RoutingAction.Proxy => 0, RoutingAction.Direct => 1, _ => 2 };
        }
        catch (Exception error) { Log?.Invoke("Policy evaluation failed: " + error.Message); return 2; }
    }

    private int Resolve(uint pid, nint query, int length, nint response, int capacity)
    {
        try
        {
            var bytes = new byte[length]; Marshal.Copy(query, bytes, 0, length);
            var result = resolveDns((int)pid, bytes);
            if (result.Length > capacity) return 0;
            Marshal.Copy(result, 0, response, result.Length); return result.Length;
        }
        catch (Exception error) { Log?.Invoke("DNS forwarding failed: " + error.Message); return 0; }
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal) return true;
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4 ? bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 ||
            bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 169 && bytes[1] == 254 : (bytes[0] & 0xfe) == 0xfc;
    }

    public void Start()
    {
        if (!loaded) throw new InvalidOperationException(nativeError);
        if (running) return;
        if (WindowsIntegration.FindLocalProxyProcesses([new ProxyServer { Host = "127.0.0.1", Port = 34010 }]).Any(pid => pid != Environment.ProcessId))
            throw new InvalidOperationException("本地转发端口 34010 已被其他程序占用。请先停止原版 ProxyBridge 的代理，再启用本工具。");
        if (!NativeStart()) throw new InvalidOperationException("WinDivert could not start. Check elevation, driver availability and the activity log.");
        if (!SetDnsBypass(false)) { var error = Marshal.GetLastWin32Error(); NativeStop(); throw new System.ComponentModel.Win32Exception(error); }
        running = true;
    }
    public void Stop()
    {
        if (!running) return;
        if (!SetDnsBypass(true)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        NativeStop(); running = false;
    }
    public void Dispose() { Stop(); foreach (var bridge in bridges) bridge.Dispose(); if (dependencyHandle != 0) { FreeLibrary(dependencyHandle); dependencyHandle = 0; } }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadLibraryEx(string path, nint reserved, uint flags);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(nint module);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PolicyCallback(uint pid,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, ushort port, int udp, out uint proxyId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DnsCallback(uint pid, nint query, int length, nint response, int capacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void LogCallback([MarshalAs(UnmanagedType.LPUTF8Str)] string message);
    [DllImport("ProxyDeskCore", EntryPoint = "ProxyDesk_SetPolicyCallback", CallingConvention = CallingConvention.Cdecl)] private static extern void SetPolicyCallback(PolicyCallback callback);
    [DllImport("ProxyDeskCore", EntryPoint = "ProxyDesk_SetDnsCallback", CallingConvention = CallingConvention.Cdecl)] private static extern void SetDnsCallback(DnsCallback callback);
    [DllImport("ProxyDeskCore", EntryPoint = "ProxyDesk_SetDnsBypass", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetDnsBypass([MarshalAs(UnmanagedType.Bool)] bool enabled);
    [DllImport("ProxyDeskCore", EntryPoint = "ProxyBridge_SetLogCallback", CallingConvention = CallingConvention.Cdecl)] private static extern void SetLogCallback(LogCallback callback);
    [DllImport("ProxyDeskCore", EntryPoint = "ProxyBridge_AddProxyConfig", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private static extern uint AddProxyConfig(int kind,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string host, ushort port, [MarshalAs(UnmanagedType.LPUTF8Str)] string user, [MarshalAs(UnmanagedType.LPUTF8Str)] string password, [MarshalAs(UnmanagedType.Bool)] bool sendDomain);
    [DllImport("ProxyDeskCore", EntryPoint = "ProxyBridge_Start", CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool NativeStart();
    [DllImport("ProxyDeskCore", EntryPoint = "ProxyBridge_Stop", CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool NativeStop();
}

[SupportedOSPlatform("windows")]
internal sealed class HttpsBridge : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource cancellation = new();
    private readonly ProxyServer proxy;
    private readonly string password;
    private readonly Action<string> report;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public HttpsBridge(ProxyServer proxy, string password, Action<string> report)
    {
        this.proxy = JsonDefaults.Clone(proxy); this.password = password;
        this.report = report;
        listener.Start(); _ = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try { while (!cancellation.IsCancellationRequested) _ = RelayAsync(await listener.AcceptTcpClientAsync(cancellation.Token)); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }
    private async Task RelayAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                if (client.Client.RemoteEndPoint is not IPEndPoint endpoint || WindowsIntegration.GetTcpClientPid(endpoint) != Environment.ProcessId)
                    throw new IOException("The local TLS bridge only accepts connections from the capture service.");
                var local = client.GetStream();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var header = await ProxyTunnel.ReadHttpHeaderAsync(local, timeout.Token);
                var parts = header.Split("\r\n")[0].Split(' ');
                if (parts.Length != 3 || parts[0] != "CONNECT") throw new IOException("Only CONNECT is accepted.");
                var uri = new Uri("http://" + parts[1]);
                await using var upstream = await ProxyTunnel.OpenAsync(proxy, password, uri.Host, uri.Port, timeout.Token);
                await local.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), timeout.Token);
                using var transfer = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                async Task UploadAsync()
                {
                    await local.CopyToAsync(upstream, transfer.Token);
                    await ((System.Net.Security.SslStream)upstream).ShutdownAsync();
                }
                async Task DownloadAsync()
                {
                    await upstream.CopyToAsync(local, transfer.Token);
                    client.Client.Shutdown(SocketShutdown.Send);
                }
                var upload = UploadAsync(); var download = DownloadAsync();
                try
                {
                    await await Task.WhenAny(upload, download);
                    await Task.WhenAll(upload, download);
                }
                finally { await transfer.CancelAsync(); }
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
            { if (!cancellation.IsCancellationRequested && error is not OperationCanceledException) report("HTTPS bridge failed: " + error.Message); }
        }
    }
    public void Dispose() { cancellation.Cancel(); listener.Stop(); cancellation.Dispose(); }
}
