using System.Diagnostics;
using System.Text.Json;
using ProxyDesk.Core;
using ProxyDesk.Platform.Windows;
using ActivityEvent = ProxyDesk.Core.ActivityEvent;

namespace ProxyDesk.Service;

public sealed class EngineService : IDisposable
{
    private readonly object stateLock = new();
    private readonly SemaphoreSlim mutations = new(1, 1);
    private readonly RuleEngine rules = new();
    private readonly DohResolver dns = new();
    private readonly ConfigurationStore store;
    private readonly ServiceRuntime runtime;
    private readonly ITrafficBackend backend;
    private readonly WindowsProcessCatalog? processes;
    private readonly Queue<ConnectionEvent> connections = new();
    private readonly Queue<ActivityEvent> activities = new();
    private readonly Dictionary<int, ManagedProcess> managed = [];
    private readonly Dictionary<Guid, ProxyTestResult> tests = [];
    private volatile bool running;
    private bool recording = true;
    private bool discovery;
    private long sequence;
    private string? lastError;
    private DateTimeOffset? startedAt;
    public string? Ssid { get; private set; }

    public EngineService(ServiceOptions options)
    {
        using var process = Process.GetCurrentProcess();
        runtime = new(Environment.ProcessId, process.StartTime.ToUniversalTime().Ticks, options.DataDirectory,
            OperatingSystem.IsWindows() && WindowsIntegration.IsAdministrator,
            OperatingSystem.IsWindows() && Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService());
        store = new ConfigurationStore(options); rules.Apply(store.Configuration);
        if (OperatingSystem.IsWindows()) { processes = new(); backend = new WindowsTrafficBackend(rules, ResolveWire, processes.GetLineage); }
        else backend = new UnavailableBackend();
        backend.Connection += AddConnection;
        backend.Log += message => Activity("Info", message);
        backend.Apply(store.Configuration, store.GetPassword);
        Activity("Info", "ProxyDesk service started.");
    }
    public void Activity(string level, string message)
    {
        lock (stateLock)
        {
            activities.Enqueue(new(Interlocked.Increment(ref sequence), DateTimeOffset.Now, level, message));
            while (activities.Count > 2000) activities.Dequeue();
        }
    }
    private void AddConnection(ConnectionEvent connection)
    {
        lock (stateLock)
        {
            if (!recording || connection.RuleId is null && connection.Action == RoutingAction.Direct && !discovery) return;
            connections.Enqueue(connection with { Sequence = Interlocked.Increment(ref sequence) });
            while (connections.Count > 10000) connections.Dequeue();
        }
    }
    public ServiceState State()
    {
        lock (stateLock)
        {
            foreach (var pid in managed.Keys.ToArray())
            {
                try { using var process = Process.GetProcessById(pid); if (process.HasExited || process.StartTime.ToUniversalTime() != managed[pid].StartedAt.UtcDateTime) managed.Remove(pid); }
                catch (ArgumentException) { managed.Remove(pid); }
            }
            return new(JsonDefaults.Clone(store.Configuration), running, startedAt, Ssid, recording, discovery,
                lastError, backend.Capabilities, connections.TakeLast(1000).Reverse().ToArray(),
                activities.TakeLast(300).Reverse().ToArray(), managed.Values.ToArray(), tests.Values.ToArray(), runtime);
        }
    }
    public async Task<RpcResponse> DispatchAsync(RpcRequest request, CancellationToken token)
    {
        if (request.Version != 1) return RpcResponse.Fail("Unsupported protocol version.");
        if (request.Operation == "GetState") return RpcResponse.Ok(State());
        await mutations.WaitAsync(token);
        try
        {
            switch (request.Operation)
            {
                case "ApplyConfiguration":
                    var update = request.Payload.Deserialize<ConfigurationUpdate>(JsonDefaults.Options)!;
                    ConfigurationValidator.Validate(update.Configuration);
                    foreach (var proxy in update.Configuration.Proxies)
                    {
                        var old = store.Configuration.Proxies.SingleOrDefault(p => p.Id == proxy.Id);
                        proxy.Revision = old is not null && old.Kind == proxy.Kind && old.Host == proxy.Host && old.Port == proxy.Port && old.Username == proxy.Username && !update.Passwords.ContainsKey(proxy.Id)
                            ? old.Revision : Guid.NewGuid();
                    }
                    backend.Apply(update.Configuration, id => update.Passwords.TryGetValue(id, out var password) ? password : store.GetPassword(id));
                    lock (stateLock) { store.Save(update.Configuration, update.Passwords); rules.Apply(store.Configuration); dns.Clear(); }
                    Activity("Info", "Configuration updated: " + store.Configuration.ActiveProfile.Name);
                    return RpcResponse.Ok(State());
                case "Start":
                    if (!running) { backend.Start(); running = true; startedAt = DateTimeOffset.Now; lastError = null; Activity("Info", "Traffic capture started."); }
                    return RpcResponse.Ok(State());
                case "Stop":
                    backend.Stop(); running = false; startedAt = null; dns.Clear(); Activity("Info", "Traffic capture stopped by user.");
                    return RpcResponse.Ok(State());
                case "Recording": recording = request.Payload.Deserialize<ToggleRequest>(JsonDefaults.Options)!.Value; return RpcResponse.Ok(State());
                case "Discovery": discovery = request.Payload.Deserialize<ToggleRequest>(JsonDefaults.Options)!.Value; return RpcResponse.Ok(State());
                case "ClearConnections": lock (stateLock) connections.Clear(); return RpcResponse.Ok(State());
                case "ClearActivities": lock (stateLock) activities.Clear(); return RpcResponse.Ok(State());
                case "TestProxy":
                    var id = request.Payload.Deserialize<ProxyTestRequest>(JsonDefaults.Options)!.ProxyId;
                    var testProxy = store.Configuration.Proxies.Single(p => p.Id == id);
                    var test = await ProxyTunnel.TestAsync(testProxy, store.GetPassword(id), store.Configuration.Settings.TestUrl, token);
                    lock (stateLock) tests[id] = test;
                    Activity(test.Success ? "Info" : "Error", $"Proxy test {testProxy.Name}: {test.Detail}");
                    return RpcResponse.Ok(test);
                default: return RpcResponse.Fail("Unknown operation: " + request.Operation);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            lastError = error.Message; Activity("Error", error.Message); return RpcResponse.Fail(error.Message);
        }
        finally { mutations.Release(); }
    }
    public bool RegisterManaged(int pid, string path)
    {
        using var process = Process.GetProcessById(pid);
        lock (stateLock) managed[pid] = new(pid, path, process.StartTime.ToUniversalTime(), true);
        return true;
    }
    public AppConfiguration? DnsConfiguration => running ? rules.Configuration : null;
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public WindowsProcessCatalog Processes => processes!;
    public bool IsDnsAttached(int pid, DateTimeOffset startedAt)
    {
        lock (stateLock) return managed.TryGetValue(pid, out var process) && process.StartedAt == startedAt && process.DnsReady;
    }
    private bool IsUpstreamProcess(int pid, AppConfiguration configuration) => pid == Environment.ProcessId ||
        OperatingSystem.IsWindows() && WindowsIntegration.FindLocalProxyProcesses(configuration.Proxies).Contains(pid);
    private IReadOnlyList<ApplicationProcess> GetLineage(int pid, string path)
        => OperatingSystem.IsWindows() ? processes!.GetLineage(pid, path) : [new(path)];
    public int DnsPolicy(string path, int pid)
    {
        if (!running) return 1;
        var snapshot = rules.Configuration;
        if (IsUpstreamProcess(pid, snapshot)) return 1;
        var decision = RuleEngine.Evaluate(snapshot, GetLineage(pid, path), AppPlatform.Windows);
        return decision.Action == RoutingAction.Block ? 2 : decision.Action == RoutingAction.Direct || !decision.StrictDns ? 1 : 0;
    }
    public async Task<(int Action, byte[] Response)> ResolveDnsAsync(int pid, string path, byte[] query, CancellationToken token, uint queryOptions = 0)
    {
        if (!running) return (1, []);
        AppConfiguration snapshot; RoutingDecision decision; ProxyServer? proxy; string password;
        lock (stateLock)
        {
            snapshot = rules.Configuration;
            if (IsUpstreamProcess(pid, snapshot)) return (1, []);
            decision = RuleEngine.Evaluate(snapshot, GetLineage(pid, path), AppPlatform.Windows);
            proxy = decision.Action == RoutingAction.Proxy ? snapshot.Proxies.Single(p => p.Id == decision.ProxyId) : null;
            password = proxy is null ? "" : store.GetPassword(proxy.Id);
        }
        if (decision.Action == RoutingAction.Direct || decision.Action == RoutingAction.Proxy && !decision.StrictDns) return (1, []);
        var name = DnsWire.QuestionName(query);
        AddConnection(new(0, DateTimeOffset.Now, "DNS", path, pid, name, 53, decision.Action, decision.ProxyId, decision.RuleId, "Application DNS via DoH"));
        if (decision.Action == RoutingAction.Block) return (2, DnsError(query, 5));
        using var clientProcess = Process.GetProcessById(pid);
        var response = await dns.ResolveAsync(query, pid, snapshot.ActiveProfileId, proxy!, password, snapshot.Settings, token,
            path + ":" + clientProcess.StartTime.ToUniversalTime().Ticks, queryOptions);
        return (0, response);
    }
    private byte[] ResolveWire(int pid, byte[] query)
    {
        using var process = Process.GetProcessById(pid);
        var path = process.MainModule?.FileName ?? throw new InvalidOperationException("Cannot identify DNS client.");
        try
        {
            var result = ResolveDnsAsync(pid, path, query, CancellationToken.None).GetAwaiter().GetResult();
            return result.Response.Length > 0 ? result.Response : DnsError(query, 2);
        }
        catch (Exception error) { Activity("Error", "DNS request failed: " + error.Message); return DnsError(query, 2); }
    }
    public static byte[] DnsError(byte[] query, int code)
    {
        var result = (byte[])query.Clone(); result[2] |= 0x80; result[3] = (byte)(0x80 | code);
        Array.Clear(result, 6, 6); return result;
    }
    public async Task UpdateNetworkAsync(string? ssid, CancellationToken token)
    {
        if (Ssid == ssid) return;
        Ssid = ssid;
        var configuration = JsonDefaults.Clone(store.Configuration);
        if (!configuration.Settings.AutomationEnabled) return;
        var mapping = configuration.Automations.SingleOrDefault(a => a.Enabled && a.Ssid == ssid);
        if (mapping is null || mapping.ProfileId == configuration.ActiveProfileId) return;
        configuration.ActiveProfileId = mapping.ProfileId;
        await DispatchAsync(RpcRequest.Create("ApplyConfiguration", new ConfigurationUpdate(configuration, [])), token);
        Activity("Info", "Wi-Fi profile switch: " + ssid);
    }
    public async Task StartConfiguredAsync(CancellationToken token)
    {
        if (store.Configuration.Settings.StartProxyOnLaunch) await DispatchAsync(RpcRequest.Create("Start"), token);
    }
    public void Dispose() { backend.Dispose(); mutations.Dispose(); }
}

internal sealed class UnavailableBackend : ITrafficBackend
{
    public BackendCapabilities Capabilities => new(false, false, false, "Linux", "The Linux capture and DNS backends are scheduled for phase two.");
    public event Action<ConnectionEvent>? Connection { add { } remove { } }
    public event Action<string>? Log { add { } remove { } }
    public void Apply(AppConfiguration configuration, Func<Guid, string> getPassword) { }
    public void Start() => throw new PlatformNotSupportedException(Capabilities.Detail);
    public void Stop() { }
    public void Dispose() { }
}
