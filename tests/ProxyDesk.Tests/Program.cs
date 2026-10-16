using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyDesk.Core;

if (args is ["--live-socks5", var liveHost, var livePort, var liveApplication, var liveReport])
{
    Environment.ExitCode = await LiveProxyCheck.RunAsync(liveHost, int.Parse(livePort), liveApplication, liveReport);
    return;
}

var count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; }
void Reject(Action action, string name) { try { action(); } catch (InvalidDataException) { Check(true, name); return; } throw new Exception("FAIL: " + name); }

var configuration = AppConfiguration.CreateDefault();
var proxy = new ProxyServer { Name = "Fixture", Host = "127.0.0.1", Port = 1080 };
configuration.Proxies.Add(proxy);
var appRule = new ApplicationRule { Name = "Example", Selector = new() { Kind = SelectorKind.FullPath, Value = @"C:\Apps\Example.exe" }, Action = RoutingAction.Proxy, ProxyId = proxy.Id };
configuration.ActiveProfile.Rules.Add(appRule);
configuration.ActiveProfile.Rules.Add(new() { Name = "Directory", Selector = new() { Kind = SelectorKind.DirectoryGlob, Value = @"C:\Apps" }, Action = RoutingAction.Block, IncludeDescendants = false });
var rules = new RuleEngine(); rules.Apply(configuration);
Check(rules.Evaluate(@"c:\apps\EXAMPLE.EXE", AppPlatform.Windows).ProxyId == proxy.Id, "Windows path matching is case-insensitive");
Check(rules.Evaluate(@"C:\Apps\Example.exe", AppPlatform.Windows).StrictDns, "Proxy rules use application DNS by default");
appRule.StrictDns = false;
ConfigurationValidator.Validate(configuration);
Check(!RuleEngine.Evaluate(configuration, @"C:\Apps\Example.exe", AppPlatform.Windows).StrictDns, "Proxy rules can use system DNS without changing the traffic action");
appRule.StrictDns = true;
var childLineage = new ApplicationProcess[] { new(@"D:\Tools\child.exe"), new(@"C:\Apps\Example.exe") };
Check(RuleEngine.Evaluate(configuration, childLineage, AppPlatform.Windows).ProxyId == proxy.Id, "Descendants inherit the parent rule and proxy");
appRule.IncludeDescendants = false;
Check(RuleEngine.Evaluate(configuration, childLineage, AppPlatform.Windows).Action == RoutingAction.Direct, "Disabling descendant routing preserves the child default");
appRule.IncludeDescendants = true;
var childOverride = new ApplicationRule { Name = "Child", Selector = new() { Value = @"D:\Tools\child.exe" }, Action = RoutingAction.Block };
configuration.ActiveProfile.Rules.Insert(0, childOverride);
Check(RuleEngine.Evaluate(configuration, childLineage, AppPlatform.Windows).Action == RoutingAction.Block, "Explicit and inherited rules follow the same list order");
configuration.ActiveProfile.Rules.Remove(childOverride);
var scriptConfiguration = AppConfiguration.CreateDefault(); scriptConfiguration.Proxies.Add(proxy);
scriptConfiguration.ActiveProfile.Rules.Add(new() { Name = "Script", Selector = new() { Kind = SelectorKind.ScriptPath, Value = @"C:\Scripts\job.ps1" }, ProxyId = proxy.Id });
Check(RuleEngine.Evaluate(scriptConfiguration, [new ApplicationProcess(@"C:\pwsh.exe", @"c:\scripts\JOB.ps1")], AppPlatform.Windows).Action == RoutingAction.Proxy, "Script rules match their actual script path");
Check(RuleEngine.Evaluate(scriptConfiguration, [new ApplicationProcess(@"C:\pwsh.exe", @"C:\Scripts\other.ps1")], AppPlatform.Windows).Action == RoutingAction.Direct, "Script rules do not affect unrelated scripts in the same interpreter");
Check(RuleEngine.Evaluate(scriptConfiguration, [new ApplicationProcess(@"C:\curl.exe"), new ApplicationProcess(@"C:\pwsh.exe", @"C:\Scripts\job.ps1")], AppPlatform.Windows).Action == RoutingAction.Proxy, "Script rules apply to spawned tools");
Check(rules.Evaluate(@"C:\Apps\Other.exe", AppPlatform.Windows).Action == RoutingAction.Block, "Directory rules match child executables");
Check(rules.Evaluate(@"C:\Else\Other.exe", AppPlatform.Windows).Action == RoutingAction.Direct, "Unmatched traffic uses the profile default");
Check(rules.Evaluate(@"C:\Apps\Example.exe", AppPlatform.Linux).Action == RoutingAction.Direct, "Selectors cannot cross operating systems");
configuration.ActiveProfile.Rules.Reverse(); rules.Apply(configuration);
Check(rules.Evaluate(@"C:\Apps\Example.exe", AppPlatform.Windows).Action == RoutingAction.Block, "The first matching rule wins");
configuration.ActiveProfile.Rules[0].Enabled = false; rules.Apply(configuration);
Check(rules.Evaluate(@"C:\Apps\Example.exe", AppPlatform.Windows).Action == RoutingAction.Proxy, "Disabled rules are skipped");
var linux = new ApplicationRule { Name = "Linux", Selector = new() { Platform = AppPlatform.Linux, Kind = SelectorKind.ExecutableName, Value = "curl" }, Action = RoutingAction.Block };
configuration.ActiveProfile.Rules.Insert(0, linux); rules.Apply(configuration);
Check(rules.Evaluate("/usr/bin/curl", AppPlatform.Linux).Action == RoutingAction.Block && rules.Evaluate("/usr/bin/CURL", AppPlatform.Linux).Action == RoutingAction.Direct, "Linux names are case-sensitive");
configuration.Proxies.Clear(); Reject(() => ConfigurationValidator.Validate(configuration), "Dangling proxy references are rejected");
configuration.Proxies.Add(proxy); proxy.Port = 65536; Reject(() => ConfigurationValidator.Validate(configuration), "Invalid ports are rejected"); proxy.Port = 1080;
configuration.Automations.Add(new() { Ssid = "Office", ProfileId = configuration.ActiveProfileId });
configuration.Automations.Add(new() { Ssid = "Office", ProfileId = configuration.ActiveProfileId });
Reject(() => ConfigurationValidator.Validate(configuration), "Duplicate SSIDs are rejected"); configuration.Automations.Clear();
configuration.Version = 2; Reject(() => ConfigurationValidator.Validate(configuration), "Unknown configuration versions are rejected"); configuration.Version = 1;
using (var memory = new MemoryStream())
{
    await FrameProtocol.WriteAsync(memory, RpcRequest.Create("GetState"), CancellationToken.None);
    memory.Position = 0; Check((await FrameProtocol.ReadAsync<RpcRequest>(memory, CancellationToken.None)).Operation == "GetState", "IPC framing roundtrips");
}
await using (var fixture = new ProxyFixture(false))
{
    var outgoing = new ProxyServer { Name = "HTTP fixture", Kind = ProxyKind.Http, Host = "127.0.0.1", Port = fixture.Port, Username = "fixture-user" };
    var result = await ProxyTunnel.TestAsync(outgoing, "fixture-password", "http://fixture.invalid/", CancellationToken.None);
    Check(result.Success && fixture.ConnectRequests == 1, "HTTP CONNECT sends traffic through the selected proxy");
    Check(fixture.Authenticated, "HTTP Basic authentication reaches the proxy");
}
await using (var fixture = new ProxyFixture(true))
{
    var outgoing = new ProxyServer { Name = "SOCKS fixture", Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = fixture.Port, Username = "fixture-user" };
    var result = await ProxyTunnel.TestAsync(outgoing, "fixture-password", "http://fixture.invalid/", CancellationToken.None);
    Check(result.Success && fixture.LastDestination == "fixture.invalid", "SOCKS5 sends a hostname without local resolution");
    Check(fixture.Authenticated, "SOCKS5 username/password authentication succeeds");
}
await using (var reject = new ProxyFixture(false, true))
{
    var outgoing = new ProxyServer { Name = "Rejected", Kind = ProxyKind.Http, Host = "127.0.0.1", Port = reject.Port };
    Check(!(await ProxyTunnel.TestAsync(outgoing, "", "http://fixture.invalid/", CancellationToken.None)).Success, "Proxy errors fail without a direct fallback");
}
using (var rsa = System.Security.Cryptography.RSA.Create(2048))
{
    var certificateRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost", rsa,
        System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
    using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var server = Task.Run(async () =>
    {
        using var accepted = await listener.AcceptTcpClientAsync();
        await using var stream = new System.Net.Security.SslStream(accepted.GetStream());
        try { await stream.AuthenticateAsServerAsync(certificate); }
        catch (Exception error) when (error is IOException or System.Security.Authentication.AuthenticationException) { }
    });
    var outgoing = new ProxyServer { Name = "Invalid TLS", Kind = ProxyKind.Https, Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
    Check(!(await ProxyTunnel.TestAsync(outgoing, "", "http://fixture.invalid/", CancellationToken.None)).Success, "HTTPS proxies reject untrusted TLS certificates");
    await server; listener.Stop();
}
var dnsQuery = BuildQuestion("example.invalid");
Check(DnsWire.QuestionName(dnsQuery) == "example.invalid", "DNS question parsing");
var resolver = new DohResolver();
await using (var first = new ProxyFixture(false))
await using (var second = new ProxyFixture(false))
{
    var firstProxy = new ProxyServer { Name = "First DNS", Kind = ProxyKind.Http, Host = "127.0.0.1", Port = first.Port };
    var secondProxy = new ProxyServer { Name = "Second DNS", Kind = ProxyKind.Http, Host = "127.0.0.1", Port = second.Port };
    var settings = new Settings { DohUrl = "http://doh.fixture.invalid/dns-query", DohBootstrapIp = "127.0.0.1" };
    var profileId = Guid.NewGuid();
    var response = await resolver.ResolveAsync(dnsQuery, 100, profileId, firstProxy, "", settings, CancellationToken.None);
    Check(response[^4] == 203 && DnsWire.MinimumAnswerTtl(response) == 60, "DoH wireformat responses preserve records and TTL");
    await resolver.ResolveAsync(dnsQuery, 100, profileId, firstProxy, "", settings, CancellationToken.None);
    Check(first.ConnectRequests == 1, "Repeated DNS queries use the application cache");
    await resolver.ResolveAsync(dnsQuery, 101, profileId, firstProxy, "", settings, CancellationToken.None);
    Check(first.ConnectRequests == 2, "DNS caches are isolated by application");
    await resolver.ResolveAsync(dnsQuery, 100, profileId, secondProxy, "", settings, CancellationToken.None);
    Check(second.ConnectRequests == 1, "DNS caches and traffic are isolated by proxy exit");
    await resolver.ResolveAsync(dnsQuery, 100, Guid.NewGuid(), firstProxy, "", settings, CancellationToken.None);
    Check(first.ConnectRequests == 3, "DNS caches are isolated by profile");
    await resolver.ResolveAsync(dnsQuery, 100, profileId, firstProxy, "", settings, CancellationToken.None, queryOptions: 8);
    Check(first.ConnectRequests == 4, "DNS_QUERY_BYPASS_CACHE performs a fresh proxy request");
    try { await resolver.ResolveAsync(dnsQuery, 999, profileId, firstProxy, "", settings, CancellationToken.None, queryOptions: 16); throw new Exception("Cache-only queries must not use the network."); }
    catch (DnsCacheMissException) { Check(first.ConnectRequests == 4, "DNS_QUERY_NO_WIRE_QUERY never performs a network request"); }
    var aged = DnsFixture.Answer(dnsQuery); DnsWire.AgeAnswerTtls(aged, 15);
    Check(DnsWire.MinimumAnswerTtl(aged) == 45, "Cached DNS TTLs decrease instead of being extended");
}
if (args.Length > 0 && args[0] == "--native")
{
    using var bypass = new EventWaitHandle(false, EventResetMode.ManualReset, @"Global\ProxyDesk.Dns.Tests.Bypass.v1");
    bypass.Reset();
    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    using (var processTests = Process.Start(new ProcessStartInfo(Path.Combine(root, "artifacts/native-tests/ProxyDesk.ProcessTests.exe"))
        { UseShellExecute = false, CreateNoWindow = true })!)
    {
        await processTests.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(processTests.ExitCode == 0, "Native process lookup handles UDP port reuse and concurrent owner tables");
    }
    await using var server = new DnsFixture();
    using var stop = new CancellationTokenSource(); var serverTask = server.RunAsync(stop.Token);
    var launcher = Path.Combine(root, "artifacts/native-tests/ProxyDesk.Launcher.exe");
    foreach (var architecture in new[] { "64", "32" })
    {
        var output = Path.Combine(root, "artifacts", "dns-probe-" + architecture + "-" + Guid.NewGuid().ToString("N") + ".txt");
        var start = new ProcessStartInfo(launcher) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        var probe = Path.Combine(root, "artifacts/native-tests/ProxyDesk.DnsProbe" + architecture + ".exe");
        start.ArgumentList.Add(probe); start.ArgumentList.Add("\"" + output + "\"");
        using var process = Process.Start(start)!; await process.WaitForExitAsync();
        Check(process.ExitCode == 0, "Managed launch succeeds for x" + architecture);
        for (var i = 0; i < 100 && !File.Exists(output + ".done"); i++) await Task.Delay(100);
        var result = File.ReadAllText(output); Console.WriteLine(result);
        Check(File.Exists(output + ".done") && result.Contains("NATIVE_DNS_PASS"), "Native DNS APIs, cancellation and child loading pass for x" + architecture);
        var attachedOutput = output + ".attached";
        var normal = new ProcessStartInfo(probe) { UseShellExecute = false, CreateNoWindow = true };
        normal.ArgumentList.Add(attachedOutput); normal.ArgumentList.Add("--wait-for-dns");
        using var application = Process.Start(normal)!;
        for (var i = 0; i < 100 && !File.Exists(attachedOutput + ".ready"); i++) await Task.Delay(20);
        var injector = new ProcessStartInfo(Path.Combine(root, "artifacts/native-tests/ProxyDesk.Injector" + architecture + ".exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        injector.ArgumentList.Add(application.Id.ToString());
        injector.ArgumentList.Add((application.StartTime.ToUniversalTime().ToFileTimeUtc() + 1).ToString());
        using (var stale = Process.Start(injector)!) {
            await stale.WaitForExitAsync();
            Check(stale.ExitCode != 0 && !application.HasExited, "Automatic DNS rejects a stale process identity for x" + architecture);
        }
        injector.ArgumentList[1] = application.StartTime.ToUniversalTime().ToFileTimeUtc().ToString();
        for (var attempt = 0; attempt < 2; attempt++) {
            using var attach = Process.Start(injector)!;
            var attachError = await attach.StandardError.ReadToEndAsync(); await attach.WaitForExitAsync();
            Check(attach.ExitCode == 0, "Runtime DNS attachment is successful and idempotent for x" + architecture + ": " + attachError);
        }
        await File.WriteAllTextAsync(attachedOutput + ".run", "run");
        await application.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        var attachedResult = await File.ReadAllTextAsync(attachedOutput); Console.WriteLine(attachedResult);
        Check(application.ExitCode == 0 && attachedResult.Contains("NATIVE_DNS_PASS"), "Normally started x" + architecture + " applications use DNS interception and pass child/API checks");
        var stoppedOutput = output + ".stopped";
        var stoppedStart = new ProcessStartInfo(launcher) { UseShellExecute = false, CreateNoWindow = true };
        stoppedStart.ArgumentList.Add(probe); stoppedStart.ArgumentList.Add("\"" + stoppedOutput + "\" --stopped-dns");
        using var stopped = Process.Start(stoppedStart)!; await stopped.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        for (var index = 0; index < 150 && !File.Exists(stoppedOutput + ".done"); index++) await Task.Delay(100);
        Check(stopped.ExitCode == 0 && File.Exists(stoppedOutput + ".done") && File.ReadAllText(stoppedOutput).Contains("STOPPED_DNS_PASS"), "Explicit stop restores system DNS for x" + architecture);
    }
    stop.Cancel(); try { await serverTask; } catch (OperationCanceledException) { }
}
Console.WriteLine($"TEST_PASS: {count} checks");

static byte[] BuildQuestion(string name)
{
    var bytes = new List<byte>(new byte[] { 0x12, 0x34, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
    foreach (var label in name.Split('.')) { bytes.Add((byte)label.Length); bytes.AddRange(Encoding.ASCII.GetBytes(label)); }
    bytes.AddRange(new byte[] { 0, 0, 1, 0, 1 }); return bytes.ToArray();
}

sealed class ProxyFixture : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource cancellation = new();
    private readonly bool socks;
    private readonly bool reject;
    private readonly Task accept;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public int ConnectRequests;
    public bool Authenticated;
    public string? LastDestination;
    public ProxyFixture(bool socks, bool reject = false) { this.socks = socks; this.reject = reject; listener.Start(); accept = RunAsync(); }
    private async Task RunAsync()
    {
        try { while (!cancellation.IsCancellationRequested) _ = HandleAsync(await listener.AcceptTcpClientAsync(cancellation.Token)); }
        catch (OperationCanceledException) { }
    }
    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream(); var token = cancellation.Token;
            try
            {
                if (socks)
                {
                    var head = new byte[2]; await stream.ReadExactlyAsync(head, token); var methods = new byte[head[1]]; await stream.ReadExactlyAsync(methods, token);
                    await stream.WriteAsync(new byte[] { 5, 2 }, token);
                    await stream.ReadExactlyAsync(head, token); var user = new byte[head[1]]; await stream.ReadExactlyAsync(user, token);
                    var one = new byte[1]; await stream.ReadExactlyAsync(one, token); var secret = new byte[one[0]]; await stream.ReadExactlyAsync(secret, token);
                    Authenticated = Encoding.UTF8.GetString(user) == "fixture-user" && Encoding.UTF8.GetString(secret) == "fixture-password";
                    await stream.WriteAsync(new byte[] { 1, 0 }, token);
                    var request = new byte[4]; await stream.ReadExactlyAsync(request, token);
                    var length = request[3] == 3 ? await ByteAsync(stream, token) : request[3] == 1 ? 4 : 16;
                    var destination = new byte[length]; await stream.ReadExactlyAsync(destination, token); await stream.ReadExactlyAsync(new byte[2], token);
                    LastDestination = request[3] == 3 ? Encoding.ASCII.GetString(destination) : new IPAddress(destination).ToString();
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 80 }, token);
                }
                else
                {
                    var connect = await ProxyTunnel.ReadHttpHeaderAsync(stream, token);
                    Authenticated = connect.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture-user:fixture-password")));
                    if (reject) { await stream.WriteAsync("HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), token); return; }
                    await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), token);
                }
                Interlocked.Increment(ref ConnectRequests);
                var header = await ProxyTunnel.ReadHttpHeaderAsync(stream, token);
                var lengthHeader = header.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                if (header.StartsWith("POST ") && lengthHeader is not null)
                {
                    var query = new byte[int.Parse(lengthHeader.Split(':')[1].Trim())]; await stream.ReadExactlyAsync(query, token);
                    var response = DnsFixture.Answer(query);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/dns-message\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n"), token);
                    await stream.WriteAsync(response, token);
                }
                else await stream.WriteAsync("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), token);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException) { }
        }
    }
    private static async Task<int> ByteAsync(Stream stream, CancellationToken token) { var one = new byte[1]; await stream.ReadExactlyAsync(one, token); return one[0]; }
    public async ValueTask DisposeAsync() { cancellation.Cancel(); listener.Stop(); await accept; cancellation.Dispose(); }
}

sealed class DnsFixture : IAsyncDisposable
{
    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream("ProxyDesk.Dns.Tests.v1", PipeDirection.InOut, 32, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try {
                await pipe.WaitForConnectionAsync(token); _ = ReplyAsync(pipe, token);
                // Exercise the busy interval while the broker creates its next pipe instance.
                await Task.Delay(2, token);
            }
            catch { pipe.Dispose(); throw; }
        }
    }
    private static async Task ReplyAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        await using (pipe)
        {
            try
            {
                var header = new byte[12]; await pipe.ReadExactlyAsync(header, token); var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
                var query = new byte[length]; if (length > 0) await pipe.ReadExactlyAsync(query, token);
                var operation = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
                if (operation is 2 or 5) query = [];
                else if (operation == 4) query = query[4..];
                if (query.Length > 0 && DnsWire.QuestionName(query).StartsWith("cancel")) await Task.Delay(500, token);
                var response = query.Length > 0 ? Answer(query) : [];
                var reply = new byte[8]; BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(4), response.Length);
                await pipe.WriteAsync(reply, token); await pipe.WriteAsync(response, token);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException) { }
        }
    }
    public static byte[] Answer(byte[] query)
    {
        var typePosition = 12;
        while (query[typePosition] != 0) typePosition += query[typePosition] + 1;
        typePosition++;
        var type = BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(typePosition, 2));
        var response = query.Take(typePosition + 4).ToList();
        response[2] = 0x81; response[3] = 0x80; response[7] = 1;
        response.AddRange(new byte[] { 0xc0, 0x0c, (byte)(type >> 8), (byte)type, 0, 1, 0, 0, 0, 60 });
        byte[] data = type == 28 ? IPAddress.Parse("2001:db8::1").GetAddressBytes() : type == 16 ? new byte[] { 2, (byte)'o', (byte)'k' } : new byte[] { 203, 0, 113, 7 };
        response.AddRange(new byte[] { 0, (byte)data.Length }); response.AddRange(data); return response.ToArray();
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
