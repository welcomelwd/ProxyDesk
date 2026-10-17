using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Encodings.Web;
using System.Text.Json;
using ProxyDesk.Core;

namespace ProxyDesk.Service;

internal sealed class DnsRequestLog(string source)
{
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private static readonly JsonSerializerOptions json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public int? Pid { get; set; }
    public string? Path { get; set; }
    public string Stage { get; set; } = "identify-client";
    private string policy = "policy=unavailable";
    private string question = "query=unavailable";

    internal static string Quote(string? value) => JsonSerializer.Serialize(value, json);

    public void SetPolicy(AppConfiguration configuration, IReadOnlyList<ApplicationProcess> lineage, RoutingDecision decision)
    {
        var profile = configuration.ActiveProfile;
        var rule = profile.Rules.SingleOrDefault(item => item.Id == decision.RuleId);
        var matchIndex = rule is null ? -1 : Enumerable.Range(0, lineage.Count).First(index =>
            (index == 0 || rule.IncludeDescendants) && RuleEngine.Matches(rule.Selector,
                rule.Selector.Kind == SelectorKind.ScriptPath ? lineage[index].ScriptPath : lineage[index].ExecutablePath, AppPlatform.Windows));
        var match = rule is null ? "profile-default" : matchIndex == 0 ? "self" : "ancestor";
        policy = $"profile={Quote(profile.Name)} profileId={profile.Id} action={decision.Action} strictDns={decision.StrictDns} match={match}";
        if (rule is not null)
            policy += $" rule={Quote(rule.Name)} ruleId={rule.Id} selectorKind={rule.Selector.Kind} selector={Quote(rule.Selector.Value)}" +
                $" includeDescendants={rule.IncludeDescendants} matchedPath={Quote(lineage[matchIndex].ExecutablePath)} matchedScript={Quote(lineage[matchIndex].ScriptPath)}";
        if (decision.Action == RoutingAction.Proxy)
        {
            var proxy = configuration.Proxies.Single(item => item.Id == decision.ProxyId);
            var doh = new Uri(configuration.Settings.DohUrl);
            policy += $" proxy={Quote(proxy.Name)} proxyId={proxy.Id} proxyKind={proxy.Kind} proxyHost={Quote(proxy.Host)} proxyPort={proxy.Port}" +
                $" doh={Quote(doh.Scheme + "://" + doh.Authority + doh.AbsolutePath)} bootstrapIp={Quote(configuration.Settings.DohBootstrapIp)}";
        }
    }

    public void SetQuery(byte[] query)
    {
        question = $"queryBytes={query.Length}";
        try
        {
            var name = DnsWire.QuestionName(query);
            var offset = 12;
            while (query[offset] != 0) offset += query[offset] + 1;
            var type = BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(offset + 1, 2));
            question += $" domain={Quote(name)} qtype={type} queryId={BinaryPrimitives.ReadUInt16BigEndian(query)}";
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        { question += $" queryParseError={Quote(error.Message)}"; }
    }

    public string Describe() => $"source={source} stage={Stage} pid={Pid?.ToString() ?? "unknown"} path={Quote(Path)} " +
        $"{question} {policy} elapsedMs={elapsed.ElapsedMilliseconds}";

    public string Failure(Exception error)
    {
        var causes = new List<string>();
        var stacks = new List<string>();
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
        {
            var codes = $"hresult=0x{cause.HResult:X8}";
            if (cause is Win32Exception native) codes += $" nativeError={native.NativeErrorCode}";
            if (cause is SocketException socket) codes += $" socketError={socket.SocketErrorCode}";
            if (cause is HttpRequestException http) codes += $" httpStatus={(int?)http.StatusCode} httpError={http.HttpRequestError}";
            causes.Add($"{cause.GetType().FullName}({codes}) {cause.Message}");
            stacks.Add($"{cause.GetType().FullName}: {cause.StackTrace}");
        }
        return $"DNS request failed: {Describe()} errors={Quote(string.Join(" -> ", causes))} stack={Quote(string.Join("\n", stacks))}";
    }
}
