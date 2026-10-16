using System.IO.Enumeration;
using System.Net;

namespace ProxyDesk.Core;

public sealed class RuleEngine
{
    private AppConfiguration configuration = AppConfiguration.CreateDefault();

    public AppConfiguration Configuration => Volatile.Read(ref configuration);

    public void Apply(AppConfiguration next)
    {
        ConfigurationValidator.Validate(next);
        Volatile.Write(ref configuration, JsonDefaults.Clone(next));
    }

    public RoutingDecision Evaluate(string path, AppPlatform platform)
        => Evaluate(Configuration, path, platform);

    public static RoutingDecision Evaluate(AppConfiguration snapshot, string path, AppPlatform platform)
        => Evaluate(snapshot, [new ApplicationProcess(path)], platform);

    public static RoutingDecision Evaluate(AppConfiguration snapshot, IReadOnlyList<ApplicationProcess> lineage, AppPlatform platform)
    {
        var profile = snapshot.ActiveProfile;
        foreach (var rule in profile.Rules)
        {
            if (!rule.Enabled || rule.Selector.Platform != platform) continue;
            if (lineage.Where((_, index) => index == 0 || rule.IncludeDescendants).Any(process =>
                Matches(rule.Selector, rule.Selector.Kind == SelectorKind.ScriptPath ? process.ScriptPath : process.ExecutablePath, platform)))
                return new(rule.Action, rule.ProxyId, rule.Id, rule.StrictDns);
        }
        return new(profile.DefaultAction, profile.DefaultProxyId, null, true);
    }

    public static bool Matches(ApplicationSelector selector, string? path, AppPlatform platform)
    {
        if (selector.Platform != platform || path is null) return false;
        var candidate = selector.Kind == SelectorKind.ExecutableName ? path.Replace('\\', '/').Split('/')[^1] : path;
        var expression = selector.Kind == SelectorKind.DirectoryGlob ? selector.Value.TrimEnd('\\', '/') + "/*" : selector.Value;
        if (platform == AppPlatform.Windows) { expression = expression.Replace('\\', '/'); candidate = candidate.Replace('\\', '/'); }
        return FileSystemName.MatchesSimpleExpression(expression, candidate, platform == AppPlatform.Windows);
    }
}

public static class ConfigurationValidator
{
    public static void Validate(AppConfiguration configuration)
    {
        if (configuration.Version != 1) throw new InvalidDataException("Unsupported configuration version.");
        if (configuration.Profiles.Count == 0 || configuration.Profiles.All(p => p.Id != configuration.ActiveProfileId))
            throw new InvalidDataException("An existing active profile is required.");
        if (configuration.Proxies.Select(p => p.Id).Distinct().Count() != configuration.Proxies.Count ||
            configuration.Profiles.Select(p => p.Id).Distinct().Count() != configuration.Profiles.Count)
            throw new InvalidDataException("Duplicate proxy or profile identifiers.");
        foreach (var proxy in configuration.Proxies)
        {
            if (!Enum.IsDefined(proxy.Kind) || string.IsNullOrWhiteSpace(proxy.Name) || string.IsNullOrWhiteSpace(proxy.Host) ||
                proxy.Port is < 1 or > 65535 || proxy.Host.Any(char.IsWhiteSpace))
                throw new InvalidDataException("A proxy requires a name, server address and valid port.");
        }
        void ValidateAction(RoutingAction action, Guid? proxyId)
        {
            if (!Enum.IsDefined(action)) throw new InvalidDataException("Unknown routing action.");
            if (action == RoutingAction.Proxy && !configuration.Proxies.Any(p => p.Id == proxyId))
                throw new InvalidDataException("A proxy action must reference an existing proxy.");
        }
        foreach (var profile in configuration.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Name)) throw new InvalidDataException("A profile name is required.");
            ValidateAction(profile.DefaultAction, profile.DefaultProxyId);
            if (profile.Rules.Select(r => r.Id).Distinct().Count() != profile.Rules.Count)
                throw new InvalidDataException("Duplicate rule identifiers.");
            foreach (var rule in profile.Rules)
            {
                if (!Enum.IsDefined(rule.Selector.Kind) || !Enum.IsDefined(rule.Selector.Platform)) throw new InvalidDataException("Unknown application selector.");
                if (string.IsNullOrWhiteSpace(rule.Selector.Value) || string.IsNullOrWhiteSpace(rule.Name))
                    throw new InvalidDataException("A rule requires a name and executable selector.");
                ValidateAction(rule.Action, rule.ProxyId);
            }
        }
        if (configuration.Automations.GroupBy(a => a.Ssid, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new InvalidDataException("An SSID can only be assigned to one profile.");
        foreach (var automation in configuration.Automations)
            if (string.IsNullOrWhiteSpace(automation.Ssid) || configuration.Profiles.All(p => p.Id != automation.ProfileId))
                throw new InvalidDataException("An automation requires an SSID and existing profile.");
        if (!Uri.TryCreate(configuration.Settings.DohUrl, UriKind.Absolute, out var doh) || doh.Scheme != "https" ||
            !IPAddress.TryParse(configuration.Settings.DohBootstrapIp, out _))
            throw new InvalidDataException("DoH requires an HTTPS URL and a bootstrap IP address.");
        if (!Uri.TryCreate(configuration.Settings.TestUrl, UriKind.Absolute, out var test) ||
            test.Scheme is not ("http" or "https"))
            throw new InvalidDataException("The test URL must use HTTP or HTTPS.");
    }
}
