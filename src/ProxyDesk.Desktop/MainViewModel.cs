using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using ProxyDesk.Core;
using ProxyDesk.Platform.Windows;

namespace ProxyDesk.Desktop;

public sealed record ProxyRow(Guid Id, string Name, string Kind, string Server, string Latency, string Health, string Authentication, string PrivateNetwork,
    double? LatencyMs, bool? TestSuccess, string Detail);
public sealed record RuleRow(Guid Id, bool Enabled, string Application, string Path, string Kind, string Action, string Proxy, string LastMatch);
public sealed record ConnectionRow(long Sequence, string Time, string Protocol, string Application, string Pid, string Destination, string Action, string Proxy, string Detail, ConnectionEvent Source);
public sealed record ActivityRow(string Time, string Level, string Message);

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ServiceClient client = new();
    private readonly CancellationTokenSource cancellation = new();
    private ServiceState? state;
    private string connectionSearch = "";
    private string activitySearch = "";
    private bool connectionSearchExclude;
    private string activityLevel = "";
    private bool connected;
    private bool busy;
    private string? error;
    private string currentPage = "仪表盘";
    private string networkSetupMessage = "";
    private bool networkSetupBusy;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? ConfigurationChanged;
    public ServiceState? State => state;
    public AppConfiguration Configuration => state?.Configuration ?? AppConfiguration.CreateDefault();
    public ObservableCollection<ProxyRow> Proxies { get; } = [];
    public ObservableCollection<RuleRow> Rules { get; } = [];
    public ObservableCollection<ConnectionRow> Connections { get; } = [];
    public ObservableCollection<ActivityRow> Activities { get; } = [];
    public bool Connected { get => connected; private set { connected = value; Changed(); NotifyStatusChanged(); } }
    public bool Busy { get => busy; private set { busy = value; Changed(); } }
    public string? Error { get => error; set { error = value; Changed(); } }
    public string CurrentPage { get => currentPage; set { currentPage = value; Changed(); } }
    public string ConnectionSearch { get => connectionSearch; set { connectionSearch = value; RefreshConnections(); } }
    public string ActivitySearch { get => activitySearch; set { activitySearch = value; RefreshActivities(); } }
    public bool ConnectionSearchExclude { get => connectionSearchExclude; set { connectionSearchExclude = value; RefreshConnections(); } }
    public string ActivityLevel { get => activityLevel; set { activityLevel = value; RefreshActivities(); } }
    public string Runtime
    {
        get
        {
            if (!Connected) return "—";
            if (state?.StartedAt is not { } started) return "00:00:00";
            var elapsed = DateTimeOffset.Now - started;
            return $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }
    }
    public string RunningText => !Connected ? "后台未连接" : state?.Running == true ? "代理已启用" : "代理未启用";
    public string ProfileName => state?.Configuration.ActiveProfile.Name ?? "General";
    public string DefaultAction => ActionName(state?.Configuration.ActiveProfile.DefaultAction ?? RoutingAction.Direct);
    public string SsidText => state?.Ssid ?? "无 Wi-Fi";
    public string ConnectionCount => (state?.Connections.Count ?? 0).ToString();
    public string AppCount => (state?.Connections.Select(c => c.ProcessPath).Distinct().Count() ?? 0).ToString();
    public string ProxyCount => (state?.Configuration.Proxies.Count ?? 0).ToString();
    public string RuleCount => (state?.Configuration.ActiveProfile.Rules.Count ?? 0).ToString();
    public string ProfileCount => (state?.Configuration.Profiles.Count ?? 1).ToString();
    public string AutomationCount => (state?.Configuration.Automations.Count ?? 0) + " 条规则";
    public string NetworkSetupMessage { get => networkSetupMessage; private set { networkSetupMessage = value; Changed(); } }
    public string BackendPermission => !Connected ? "未连接" : state?.Runtime is { } runtime ? runtime.IsWindowsService ? "Windows 系统服务" : runtime.IsElevated ? "管理员后台" : "普通用户后台" : "后台服务";
    public string NetworkStatus => !Connected ? "后台未连接" : state?.Capabilities.TrafficCapture != true ? OperatingSystem.IsWindows() ? "网络组件不可用" : "Linux 网络接管待接入" : NeedsNetworkAuthorization ? "等待管理员授权" : "网络组件已就绪";
    public string NetworkDetails => !Connected ? PortableSession.Enabled ? "请授权启动便携后台；配置保存在程序目录的 Data 文件夹。" : "请启动后台服务；完整包包含所需的网络组件。" :
        state?.Capabilities.TrafficCapture != true ? state?.Capabilities.Detail + (OperatingSystem.IsWindows() ? " 请重新解压完整便携包或修复安装。" : "") :
        OperatingSystem.IsWindows() ? PortableSession.Enabled ? PortableSession.Owns(state.Runtime) ? "便携后台按临时进程运行，配置保存在 Data 文件夹。" :
            "当前连接本机已有后台，配置目录见“关于”页。" : "WinDivert 随完整包内置，网络接管由后台处理。" : state.Capabilities.Detail;
    public string DataDirectoryText => state?.Runtime?.DataDirectory ?? (PortableSession.Enabled ? PortableSession.DataDirectory : "后台未连接");
    public bool CanToggleProxy => Connected && (state?.Running == true || state?.Capabilities.TrafficCapture == true);
    public bool NeedsNetworkAuthorization => OperatingSystem.IsWindows() && (!Connected || state?.Capabilities.TrafficCapture == true && state?.Runtime?.IsElevated != true);
    public bool CanPrepareNetwork => NeedsNetworkAuthorization && !networkSetupBusy;
    public string ProxyActionText => !Connected ? "后台未连接" : state?.Running == true ? "停止" : state?.Capabilities.TrafficCapture != true ? "组件不可用" : NeedsNetworkAuthorization ? "授权并启用" : "启用代理";

    public async Task ToggleProxyAsync()
    {
        if (!Connected) throw new InvalidOperationException("后台服务未连接，请在设置中启动后台。");
        if (state?.Running == true) await OperationAsync("Stop");
        else if (NeedsNetworkAuthorization) await PrepareNetworkAsync();
        else await OperationAsync("Start");
    }

    public async Task PrepareNetworkAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Linux 网络后端将在第二阶段提供。");
        if (networkSetupBusy) return;
        networkSetupBusy = true; Changed(nameof(CanPrepareNetwork)); Error = null;
        try
        {
            var directory = PortableSession.Enabled ? PortableSession.CreateOperationDirectory() : WindowsNetworkSetup.CreateOperationDirectory();
            var runtime = state?.Runtime;
            if (!Connected && runtime is not null) runtime = runtime with { ProcessId = 0, StartTimeUtcTicks = 0 };
            var request = PortableSession.Enabled ? PortableSession.CreateRequest(runtime) : WindowsNetworkSetup.CreateRequest(runtime, false, null);
            NetworkSetupMessage = "请在 Windows UAC 确认授权；随后使用现有配置启动后台并启用代理。";
            var result = await WindowsNetworkSetup.ElevateAsync(request, directory,
                Path.Combine(AppContext.BaseDirectory, "ProxyDesk.Service.exe"), cancellation.Token);
            NetworkSetupMessage = result.Message;
            if (!result.Success) Error = result.Message;
            else Error = null;
            if (result.ServiceProcessId != 0) SetState((await client.CallAsync("GetState", cancellationToken: cancellation.Token)).Read<ServiceState>());
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            NetworkSetupMessage = "网络组件准备失败：" + failure.GetBaseException().Message;
            Error = NetworkSetupMessage;
        }
        finally { networkSetupBusy = false; Changed(nameof(CanPrepareNetwork)); }
    }

    public async Task InitializeAsync()
    {
        if (PortableSession.Enabled && OperatingSystem.IsWindows())
        {
            try { SetState((await client.CallAsync("GetState", cancellationToken: cancellation.Token)).Read<ServiceState>()); }
            catch (Exception failure) when (failure is IOException or TimeoutException or OperationCanceledException)
            { await PrepareNetworkAsync(); }
        }
        await PollAsync();
    }

    public async Task ExitAsync()
    {
        if (!Connected) return;
        if (PortableSession.Owns(state?.Runtime))
        {
            using var process = Process.GetProcessById(state!.Runtime!.ProcessId);
            await client.CallAsync("Shutdown", new ShutdownRequest(process.Id), cancellation.Token);
            await process.WaitForExitAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15), cancellation.Token);
        }
        else await OperationAsync("Stop");
    }

    public async Task PollAsync()
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                var response = await client.CallAsync("GetState", cancellationToken: cancellation.Token);
                SetState(response.Read<ServiceState>());
            }
            catch (Exception failure) when (failure is IOException or TimeoutException or OperationCanceledException or InvalidOperationException)
            {
                Connected = false;
                if (!cancellation.IsCancellationRequested && Error is null) Error = "后台服务未连接：" + failure.Message;
            }
            try { await Task.Delay(1000, cancellation.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void SetState(ServiceState next)
    {
        var configurationChanged = state is null || System.Text.Json.JsonSerializer.Serialize(state.Configuration, JsonDefaults.Options) !=
            System.Text.Json.JsonSerializer.Serialize(next.Configuration, JsonDefaults.Options);
        state = next;
        Connected = true;
        var proxyRows = new List<ProxyRow>();
        foreach (var proxy in next.Configuration.Proxies)
        {
            var test = next.ProxyTests.FirstOrDefault(t => t.ProxyId == proxy.Id);
            proxyRows.Add(new(proxy.Id, proxy.Name, proxy.Kind.ToString().ToUpperInvariant(), $"{proxy.Host}:{proxy.Port}",
                test?.LatencyMs is { } latency ? $"{latency:N0} ms" : test is { Success: false } ? "失败" : "—",
                test is null ? "未测试" : test.Success ? "连接正常" : "连接失败", proxy.Username.Length > 0 || proxy.HasPassword ? "已配置" : "—", proxy.AllowPrivateNetworks ? "允许" : "直连",
                test?.LatencyMs, test?.Success, test?.Detail ?? "尚无测试结果。"));
        }
        Replace(Proxies, proxyRows);
        var ruleRows = new List<RuleRow>();
        foreach (var rule in next.Configuration.ActiveProfile.Rules)
        {
            var match = next.Connections.FirstOrDefault(c => c.RuleId == rule.Id);
            ruleRows.Add(new(rule.Id, rule.Enabled, rule.Name, rule.Selector.Value,
                rule.Selector.Kind == SelectorKind.ExecutableName ? "程序名" : rule.Selector.Kind == SelectorKind.DirectoryGlob ? "目录" : rule.Selector.Kind == SelectorKind.ScriptPath ? "脚本" : "应用",
                ActionName(rule.Action), next.Configuration.Proxies.FirstOrDefault(p => p.Id == rule.ProxyId)?.Name ?? "—",
                match?.Time.ToLocalTime().ToString("HH:mm:ss") ?? "—"));
        }
        Replace(Rules, ruleRows);
        RefreshConnections(); RefreshActivities();
        Changed(nameof(State)); Changed(nameof(ProfileName)); Changed(nameof(DefaultAction)); Changed(nameof(DataDirectoryText));
        Changed(nameof(SsidText)); Changed(nameof(ConnectionCount)); Changed(nameof(AppCount)); Changed(nameof(ProxyCount)); Changed(nameof(RuleCount));
        Changed(nameof(ProfileCount)); Changed(nameof(AutomationCount));
        if (configurationChanged) ConfigurationChanged?.Invoke();
    }

    private void NotifyStatusChanged()
    {
        Changed(nameof(Runtime)); Changed(nameof(RunningText)); Changed(nameof(BackendPermission)); Changed(nameof(NetworkStatus)); Changed(nameof(NetworkDetails));
        Changed(nameof(CanToggleProxy)); Changed(nameof(NeedsNetworkAuthorization));
        Changed(nameof(CanPrepareNetwork)); Changed(nameof(ProxyActionText));
    }

    private void RefreshConnections()
    {
        if (state is null) return;
        var rows = new List<ConnectionRow>();
        foreach (var connection in state.Connections.Where(c =>
            connectionSearch.Length == 0 || (c.ProcessPath + " " + c.Destination + " " + c.Pid).Contains(connectionSearch, StringComparison.OrdinalIgnoreCase) != connectionSearchExclude))
            rows.Add(new(connection.Sequence, connection.Time.ToLocalTime().ToString("HH:mm:ss"), connection.Protocol,
                connection.ProcessPath.Replace('\\', '/').Split('/')[^1], connection.Pid.ToString(), connection.Destination.Contains(':') ? $"[{connection.Destination}]:{connection.Port}" : $"{connection.Destination}:{connection.Port}",
                ActionName(connection.Action), state.Configuration.Proxies.FirstOrDefault(p => p.Id == connection.ProxyId)?.Name ?? "—", connection.Detail, connection));
        Replace(Connections, rows);
    }
    private void RefreshActivities()
    {
        if (state is null) return;
        var rows = new List<ActivityRow>();
        foreach (var activity in state.Activities.Where(a => (activityLevel.Length == 0 || a.Level.Equals(activityLevel, StringComparison.OrdinalIgnoreCase) ||
            activityLevel == "Warning" && a.Level.Equals("Warn", StringComparison.OrdinalIgnoreCase)) && (a.Level + " " + a.Message).Contains(activitySearch, StringComparison.OrdinalIgnoreCase)))
            rows.Add(new(activity.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), activity.Level, activity.Message));
        Replace(Activities, rows);
    }
    public async Task RunAsync(Func<Task> action)
    {
        if (Busy) return;
        Busy = true;
        try { await action(); }
        catch (Exception failure) when (failure is not OutOfMemoryException) { Error = failure.GetBaseException().Message; }
        finally { Busy = false; }
    }
    public async Task SaveAsync(AppConfiguration configuration, Dictionary<Guid, string>? passwords = null)
    {
        var response = await client.CallAsync("ApplyConfiguration", new ConfigurationUpdate(configuration, passwords ?? []));
        SetState(response.Read<ServiceState>());
    }
    public async Task OperationAsync(string operation, object? payload = null)
    {
        var response = await client.CallAsync(operation, payload);
        if (operation != "TestProxy") SetState(response.Read<ServiceState>());
    }
    public async Task TestAsync(Guid? id = null)
    {
        foreach (var proxy in Configuration.Proxies.Where(p => id is null || p.Id == id)) await OperationAsync("TestProxy", new ProxyTestRequest(proxy.Id));
        SetState((await client.CallAsync("GetState")).Read<ServiceState>());
    }
    public async Task ToggleRuleAsync(Guid id, bool enabled)
    {
        var configuration = JsonDefaults.Clone(Configuration); configuration.ActiveProfile.Rules.Single(r => r.Id == id).Enabled = enabled;
        await SaveAsync(configuration);
    }
    public static string ActionName(RoutingAction action) => action switch { RoutingAction.Proxy => "代理", RoutingAction.Direct => "直连", _ => "阻止" };
    private static void Replace<T>(ObservableCollection<T> collection, IReadOnlyList<T> rows)
    {
        if (collection.SequenceEqual(rows)) return;
        for (var index = 0; index < rows.Count; index++)
        {
            if (index >= collection.Count) collection.Add(rows[index]);
            else if (!EqualityComparer<T>.Default.Equals(collection[index], rows[index])) collection[index] = rows[index];
        }
        while (collection.Count > rows.Count) collection.RemoveAt(collection.Count - 1);
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public void Dispose() { cancellation.Cancel(); cancellation.Dispose(); }
}
