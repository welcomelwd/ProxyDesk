using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ProxyDesk.Core;

internal static class LiveProxyCheck
{
    public static async Task<int> RunAsync(string host, int port, string application, string reportPath)
    {
        if (!File.Exists(application)) throw new FileNotFoundException("The test application does not exist.", application);
        var proxy = new ProxyServer { Name = $"本机 SOCKS5 {port}", Host = host, Port = port };
        var configuration = AppConfiguration.CreateDefault(); configuration.Proxies.Add(proxy);
        configuration.Settings.AutomationEnabled = false;
        configuration.ActiveProfile.Rules.Add(new() { Name = "Telegram", ProxyId = proxy.Id,
            Selector = new() { Platform = AppPlatform.Windows, Kind = SelectorKind.FullPath, Value = Path.GetFullPath(application) } });
        ConfigurationValidator.Validate(configuration);
        reportPath = Path.GetFullPath(reportPath); Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(reportPath)!, "config.json"), JsonSerializer.Serialize(configuration, JsonDefaults.Options));

        var checks = new List<object>(); var failures = 0;
        async Task CheckAsync(string name, Func<CancellationToken, Task<string>> action)
        {
            var timer = Stopwatch.StartNew();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                var detail = await action(timeout.Token);
                checks.Add(new { name, success = true, elapsedMs = timer.ElapsedMilliseconds, detail });
                Console.WriteLine($"LIVE_PASS: {name}: {detail}");
            }
            catch (Exception error)
            {
                failures++; var detail = error.GetBaseException().Message;
                checks.Add(new { name, success = false, elapsedMs = timer.ElapsedMilliseconds, detail });
                Console.WriteLine($"LIVE_FAIL: {name}: {detail}");
            }
        }
        foreach (var url in new[] { configuration.Settings.TestUrl, "https://telegram.org/" })
            await CheckAsync("SOCKS5 TCP " + url, async token =>
            {
                var result = await ProxyTunnel.TestAsync(proxy, "", url, token);
                if (!result.Success) throw new IOException(result.Detail);
                return result.Detail;
            });
        var query = Question("telegram.org");
        await CheckAsync("DoH through the selected SOCKS5 proxy", async token =>
        {
            var resolver = new DohResolver();
            var response = await resolver.ResolveAsync(query, Environment.ProcessId, configuration.ActiveProfileId, proxy, "", configuration.Settings, token);
            if ((response[3] & 15) != 0 || BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6, 2)) == 0)
                throw new InvalidDataException("The live DoH server did not return a successful DNS answer.");
            return $"DNS answer received; minimum TTL {DnsWire.MinimumAnswerTtl(response)} s; TLS certificate validation enabled.";
        });
        await CheckAsync("SOCKS5 UDP ASSOCIATE and relayed DNS datagram", token => CheckUdpAsync(proxy, query, token));
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            recordedAt = DateTimeOffset.Now, proxy = $"socks5://{host}:{port}", application,
            scope = "Upstream transport and resolver checks only. Telegram capture and application DNS isolation were not exercised.",
            checks
        }, JsonDefaults.Options));
        Console.WriteLine($"LIVE_REPORT: {reportPath}");
        return failures == 0 ? 0 : 1;
    }

    private static byte[] Question(string name)
    {
        var query = new List<byte>(new byte[] { 0x37, 0x91, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        foreach (var label in name.Split('.')) { query.Add((byte)label.Length); query.AddRange(Encoding.ASCII.GetBytes(label)); }
        query.AddRange(new byte[] { 0, 0, 1, 0, 1 }); return query.ToArray();
    }
    private static async Task<string> CheckUdpAsync(ProxyServer proxy, byte[] query, CancellationToken token)
    {
        using var control = new TcpClient(); await control.ConnectAsync(proxy.Host, proxy.Port, token);
        var stream = control.GetStream(); await ProxyTunnel.SocksAuthenticateAsync(stream, "", "", token);
        await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, token);
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        if (header[0] != 5 || header[1] != 0) throw new IOException($"SOCKS5 UDP ASSOCIATE failed: {header[1]}.");
        var address = new byte[header[3] switch { 1 => 4, 4 => 16, _ => throw new IOException("The live check requires an IP relay address.") }];
        await stream.ReadExactlyAsync(address, token); var port = new byte[2]; await stream.ReadExactlyAsync(port, token);
        var relay = new IPAddress(address);
        if (relay.Equals(IPAddress.Any) || relay.Equals(IPAddress.IPv6Any)) relay = ((IPEndPoint)control.Client.RemoteEndPoint!).Address;
        using var udp = new UdpClient(relay.AddressFamily); udp.Connect(relay, BinaryPrimitives.ReadUInt16BigEndian(port));
        var packet = new byte[] { 0, 0, 0, 1, 1, 1, 1, 1, 0, 53 }.Concat(query).ToArray();
        await udp.SendAsync(packet, token);
        var reply = (await udp.ReceiveAsync(token)).Buffer;
        if (reply.Length < 4 || reply[0] != 0 || reply[1] != 0 || reply[2] != 0) throw new InvalidDataException("Invalid SOCKS5 UDP response.");
        var offset = reply[3] switch { 1 => 10, 4 => 22, 3 when reply.Length > 4 => 7 + reply[4], _ => throw new InvalidDataException("Invalid UDP address type.") };
        if (reply.Length < offset + 12 || reply[offset] != query[0] || reply[offset + 1] != query[1] ||
            (reply[offset + 2] & 0x80) == 0 || (reply[offset + 3] & 15) != 0)
            throw new InvalidDataException("The relayed DNS response does not match the live query.");
        return $"A DNS datagram sent through relay {relay} returned successfully.";
    }
}
