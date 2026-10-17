using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProxyDesk.Core;

public enum ProxyKind { Socks5, Http, Https }
public enum RoutingAction { Proxy, Direct, Block }
public enum AppPlatform { Windows, Linux }
public enum SelectorKind { ExecutableName, FullPath, DirectoryGlob, ScriptPath }

public sealed record ProxyServer
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid Revision { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public ProxyKind Kind { get; set; } = ProxyKind.Socks5;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 1080;
    public string Username { get; set; } = "";
    public bool HasPassword { get; set; }
    public bool AllowPrivateNetworks { get; set; }
}

public sealed record ApplicationSelector
{
    public AppPlatform Platform { get; set; } = AppPlatform.Windows;
    public SelectorKind Kind { get; set; } = SelectorKind.FullPath;
    public string Value { get; set; } = "";
}

public sealed record ApplicationRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public ApplicationSelector Selector { get; set; } = new();
    public RoutingAction Action { get; set; } = RoutingAction.Proxy;
    public Guid? ProxyId { get; set; }
    public bool Enabled { get; set; } = true;
    public bool StrictDns { get; set; } = true;
    public bool IncludeDescendants { get; set; } = true;
}

public sealed record Profile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "General";
    public RoutingAction DefaultAction { get; set; } = RoutingAction.Direct;
    public Guid? DefaultProxyId { get; set; }
    public List<ApplicationRule> Rules { get; set; } = [];
}

public sealed record AutomationRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Ssid { get; set; } = "";
    public Guid ProfileId { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed record Settings
{
    public string Theme { get; set; } = "System";
    public bool LaunchAtLogin { get; set; }
    public bool StartProxyOnLaunch { get; set; }
    public bool AutomationEnabled { get; set; } = true;
    public string TestUrl { get; set; } = "https://www.gstatic.com/generate_204";
    public string DohUrl { get; set; } = "https://cloudflare-dns.com/dns-query";
    public string DohBootstrapIp { get; set; } = "1.1.1.1";
}

public sealed record AppConfiguration
{
    public int Version { get; set; } = 1;
    public List<ProxyServer> Proxies { get; set; } = [];
    public List<Profile> Profiles { get; set; } = [new()];
    public Guid ActiveProfileId { get; set; }
    public List<AutomationRule> Automations { get; set; } = [];
    public Settings Settings { get; set; } = new();

    public static AppConfiguration CreateDefault()
    {
        var configuration = new AppConfiguration();
        configuration.ActiveProfileId = configuration.Profiles[0].Id;
        return configuration;
    }

    [JsonIgnore] public Profile ActiveProfile => Profiles.Single(p => p.Id == ActiveProfileId);
}

public sealed record RoutingDecision(RoutingAction Action, Guid? ProxyId, Guid? RuleId, bool StrictDns);
public sealed record ApplicationProcess(string ExecutablePath, string? ScriptPath = null);
public sealed record ConnectionEvent(long Sequence, DateTimeOffset Time, string Protocol, string ProcessPath,
    int Pid, string Destination, int Port, RoutingAction Action, Guid? ProxyId, Guid? RuleId, string Detail);
public sealed record ActivityEvent(long Sequence, DateTimeOffset Time, string Level, string Message);
public sealed record ProxyTestResult(Guid ProxyId, bool Success, double? LatencyMs, bool? UdpSupported, string Detail);
public sealed record BackendCapabilities(bool TrafficCapture, bool Ipv6, string Platform, string Detail);
public sealed record ServiceRuntime(int ProcessId, long StartTimeUtcTicks, string DataDirectory, bool IsElevated, bool IsWindowsService);
public sealed record ServiceState(AppConfiguration Configuration, bool Running, DateTimeOffset? StartedAt,
    string? Ssid, bool Recording, bool Discovery, string? LastError, BackendCapabilities Capabilities,
    IReadOnlyList<ConnectionEvent> Connections, IReadOnlyList<ActivityEvent> Activities,
    IReadOnlyList<ProxyTestResult> ProxyTests, ServiceRuntime? Runtime = null);

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}
