using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using ProxyDesk.Core;
using ProxyDesk.Service;
using ProxyDesk.Platform.Windows;
using ActivityEvent = ProxyDesk.Core.ActivityEvent;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static class DnsLogTests
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var options = new ServiceOptions(["--console", "--data-dir", Path.Combine(root, "dns-logs")]);
        using var engine = new EngineService(options);
        using (var journal = new ActivityLogWorker(engine, options))
        {
            var journalPath = Path.Combine(options.DataDirectory, "activity.log");
            await File.WriteAllTextAsync(journalPath, new string('x', 5 * 1024 * 1024));
            await journal.StartAsync(CancellationToken.None);
            engine.Activity("Error", "DNS journal fixture: source=udp-capture queryId=4660");
            using var journalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!File.Exists(journalPath + ".1") || !File.Exists(journalPath))
                await Task.Delay(20, journalTimeout.Token);
            async Task<string> ReadJournal()
            {
                await using var file = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(file);
                return await reader.ReadToEndAsync(journalTimeout.Token);
            }
            while (!(await ReadJournal()).Contains("DNS journal fixture")) await Task.Delay(20, journalTimeout.Token);
            await journal.StopAsync(CancellationToken.None);
            check(File.Exists(journalPath + ".1") && new FileInfo(journalPath + ".1").Length >= 5 * 1024 * 1024,
                "Activity journal preserves error sources and rotates bounded files independently of packet callbacks");
        }
        var childStart = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "[Console]::ReadLine() | Out-Null" }) childStart.ArgumentList.Add(argument);
        using var child = Process.Start(childStart)!;
        try
        {
            var path = childStart.FileName;
            var coldLineage = new WindowsProcessCatalog().GetLineage(child.Id, path);
            check(coldLineage[0].ExecutablePath.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) &&
                coldLineage.Skip(1).Any(item => item.ExecutablePath.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)),
                "A newly started child resolves its identity and parent without a WMI scan on the packet path");
            var query = new byte[] { 0x12, 0x34, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 7 }
                .Concat("example"u8.ToArray()).Concat(new byte[] { 7 }).Concat("invalid"u8.ToArray()).Concat(new byte[] { 0, 0, 1, 0, 1 }).ToArray();
            await using var upstream = new DnsLogProxy();
            var proxy = new ProxyServer { Name = "Diagnostic proxy", Kind = ProxyKind.Http, Host = "127.0.0.1", Port = upstream.Port, Username = "private-user" };
            var configuration = AppConfiguration.CreateDefault(); configuration.Proxies.Add(proxy);
            configuration.Settings.DohUrl = "https://doh.fixture.invalid/dns-query?token=private-token";
            // Exercise the DNS broker without starting the system-wide capture driver.
            typeof(EngineService).GetField("running", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(engine, true);
            async Task Apply(bool localHttp = false)
            {
                var result = await engine.DispatchAsync(RpcRequest.Create("ApplyConfiguration", new ConfigurationUpdate(configuration,
                    new() { [proxy.Id] = "private-password" })), CancellationToken.None);
                check(result.Success, "DNS diagnostic fixture configuration applies");
                // The local wire-format fixture uses HTTP to avoid modifying Windows certificate trust.
                if (localHttp) engine.DnsConfiguration!.Settings.DohUrl = "http://doh.fixture.invalid/dns-query";
                await engine.DispatchAsync(RpcRequest.Create("ClearActivities"), CancellationToken.None);
            }
            ActivityEvent Single(string prefix) => engine.State().Activities.Single(item => item.Message.StartsWith(prefix, StringComparison.Ordinal));
            async Task RequestFails()
            {
                try { await engine.ResolveDnsAsync(child.Id, path, query, CancellationToken.None); throw new Exception("The DNS fixture unexpectedly succeeded."); }
                catch (HttpRequestException) { }
            }

            await Apply();
            await engine.DispatchAsync(RpcRequest.Create("Recording", new ToggleRequest(false)), CancellationToken.None);
            var direct = await engine.ResolveDnsAsync(child.Id, path, query, CancellationToken.None);
            var bypass = Single("DNS request bypassed:");
            check(direct.Action == 1 && direct.Response.Length == 0 && bypass.Message.Contains("match=profile-default") &&
                bypass.Message.Contains("action=Direct") && bypass.Message.Contains($"pid={child.Id}") && bypass.Message.Contains("example.invalid"),
                "Unmatched applications keep system DNS and retain diagnostics while connection recording is paused");

            configuration.ActiveProfile.DefaultAction = RoutingAction.Proxy; configuration.ActiveProfile.DefaultProxyId = proxy.Id;
            await Apply(); upstream.RejectConnect = true;
            await RequestFails();
            var failure = Single("DNS request failed:");
            check(failure.Level == "Error" && failure.Message.Contains("stage=resolve-doh") && failure.Message.Contains("match=profile-default") &&
                failure.Message.Contains("qtype=1") && failure.Message.Contains("queryId=4660") && failure.Message.Contains("proxyPort=" + upstream.Port) &&
                failure.Message.Contains("doh.fixture.invalid/dns-query") && failure.Message.Contains("System.Net.Http.HttpRequestException") &&
                failure.Message.Contains("System.IO.IOException") && failure.Message.Contains("stack=") && failure.Message.Contains("elapsedMs="),
                "Proxy failures retain the default policy, DNS question, endpoint, exception chain and source stack exactly once");
            check(!failure.Message.Contains("private-password") && !failure.Message.Contains("private-user") && !failure.Message.Contains("private-token"),
                "DNS diagnostics omit proxy credentials and DoH query tokens");

            var parentRule = new ApplicationRule { Name = "Inherited DNS fixture", Selector = new() { Kind = SelectorKind.FullPath, Value = Environment.ProcessPath! }, ProxyId = proxy.Id };
            configuration.ActiveProfile.Rules.Add(parentRule);
            await Apply(true); upstream.RejectConnect = false; upstream.HttpStatus = 503;
            engine.Processes.Refresh();
            await RequestFails();
            failure = Single("DNS request failed:");
            check(failure.Message.Contains("httpStatus=503") && failure.Message.Contains("match=ancestor") &&
                failure.Message.Contains("ruleId=" + parentRule.Id) && failure.Message.Contains("matchedPath=") && failure.Message.Contains("includeDescendants=True"),
                "An inherited rule reports its actual ancestor and the upstream HTTP status");

            await Apply(true); upstream.HttpStatus = 200;
            var negative = await engine.ResolveDnsAsync(child.Id, path, query, CancellationToken.None);
            var answer = Single("DNS upstream response:");
            check(negative.Action == 0 && (negative.Response[3] & 15) == 3 && answer.Level == "Warning" && answer.Message.Contains("rcode=3 dnsStatus=9003 status=NXDOMAIN") &&
                answer.Message.Contains("match=ancestor"),
                "A real negative DNS wire response remains unchanged and records NXDOMAIN with the inherited rule");
            var childRule = new ApplicationRule { Name = "Child override", Selector = new() { Value = path }, Action = RoutingAction.Block };
            configuration.ActiveProfile.Rules.Insert(0, childRule);
            await Apply();
            var blocked = await engine.ResolveDnsAsync(child.Id, path, query, CancellationToken.None);
            check(blocked.Action == 2 && (blocked.Response[3] & 15) == 5 && Single("DNS request blocked:").Message.Contains("match=self"),
                "An explicit blocking rule reports local refusal separately from an upstream DNS error");
            childRule.Action = RoutingAction.Direct;
            await Apply();
            direct = await engine.ResolveDnsAsync(child.Id, path, query, CancellationToken.None);
            check(direct.Action == 1 && Single("DNS request bypassed:").Message.Contains("match=self"), "An explicit direct rule retains system DNS and its own rule source");
            configuration.ActiveProfile.Rules.Clear();
            await Apply();
            try { await engine.ResolveDnsAsync(child.Id, path, new byte[12], CancellationToken.None); throw new Exception("An invalid DNS question unexpectedly succeeded."); }
            catch (InvalidDataException)
            {
                check(Single("DNS request failed:").Message.Contains("stage=parse-query") && Single("DNS request failed:").Message.Contains("queryParseError="),
                    "Malformed DNS questions retain the original error and a diagnostic parsing hint");
            }
            await engine.DispatchAsync(RpcRequest.Create("ClearActivities"), CancellationToken.None);
            var wire = (byte[])typeof(EngineService).GetMethod("ResolveWire", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine, [int.MaxValue, query])!;
            check((wire[3] & 15) == 2 && Single("DNS request failed:").Message.Contains("source=packet-dns stage=identify-client pid=2147483647"),
                "Packet DNS client lookup failures are logged with their PID before returning SERVFAIL");

        }
        finally
        {
            child.StandardInput.Close();
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync();
        }
    }
}

sealed class DnsLogProxy : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task server;
    public bool RejectConnect { get; set; }
    public int HttpStatus { get; set; } = 200;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public DnsLogProxy() { listener.Start(); server = RunAsync(); }
    private async Task RunAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                var stream = client.GetStream();
                await ProxyTunnel.ReadHttpHeaderAsync(stream, stop.Token);
                if (RejectConnect) { await stream.WriteAsync("HTTP/1.1 502 Fixture failure\r\n\r\n"u8.ToArray(), stop.Token); continue; }
                await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), stop.Token);
                var header = await ProxyTunnel.ReadHttpHeaderAsync(stream, stop.Token);
                var length = int.Parse(header.Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                var query = new byte[length]; await stream.ReadExactlyAsync(query, stop.Token);
                var response = EngineService.DnsError(query, 3);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {HttpStatus} Fixture\r\nContent-Type: application/dns-message\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
                await stream.WriteAsync(response, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync() { await stop.CancelAsync(); listener.Stop(); await server; stop.Dispose(); }
}
