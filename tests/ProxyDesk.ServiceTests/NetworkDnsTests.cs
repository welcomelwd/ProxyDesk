using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using ProxyDesk.Core;
using ProxyDesk.Service;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static class NetworkDnsTests
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var options = new ServiceOptions(["--console", "--data-dir", Path.Combine(root, "network-dns")]);
        using var engine = new EngineService(options);
        var childStart = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        foreach (var argument in new[] { "-NoProfile", "-Command", "[Console]::ReadLine() | Out-Null" }) childStart.ArgumentList.Add(argument);
        using var child = Process.Start(childStart)!;
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var sockets = new List<TcpClient>();
        var proxy = new ProxyServer { Name = "Stalled DNS fixture", Kind = ProxyKind.Http, Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        var configuration = AppConfiguration.CreateDefault(); configuration.Proxies.Add(proxy);
        configuration.ActiveProfile.DefaultAction = RoutingAction.Proxy; configuration.ActiveProfile.DefaultProxyId = proxy.Id;
        async Task Apply()
        {
            var result = await engine.DispatchAsync(RpcRequest.Create("ApplyConfiguration", new ConfigurationUpdate(configuration, [])), CancellationToken.None);
            if (!result.Success) throw new Exception(result.Error);
            engine.DnsConfiguration!.Settings.DohUrl = "http://fixture.invalid/dns-query";
        }
        typeof(EngineService).GetField("running", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(engine, true);
        var query = new byte[] { 1, 2, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 7 }.Concat("example"u8.ToArray()).Concat(new byte[] { 0, 0, 1, 0, 1 }).ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var requests = new List<Task<(int Action, byte[] Response)>>();
        try
        {
            await Apply();
            for (var i = 0; i < 8; i++)
            {
                requests.Add(engine.ResolveDnsAsync(child.Id, childStart.FileName, query, deadline.Token));
                var socket = await listener.AcceptTcpClientAsync(deadline.Token); sockets.Add(socket);
                await ProxyTunnel.ReadHttpHeaderAsync(socket.GetStream(), deadline.Token);
                await socket.GetStream().WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), deadline.Token);
            }
            var timer = Stopwatch.StartNew();
            var overloaded = await engine.ResolveDnsAsync(child.Id, childStart.FileName, query, deadline.Token);
            check((overloaded.Response[3] & 15) == 2 && timer.ElapsedMilliseconds < 1000,
                "A saturated proxy DNS resolver rejects additional work immediately with SERVFAIL");
            var shared = await engine.ResolveDnsAsync(child.Id, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "svchost.exe"), query, deadline.Token);
            check(shared.Action == 1 && shared.Response.Length == 0,
                "Shared Windows DNS is passed through even when the profile defaults to proxy");
            configuration.ActiveProfile.DefaultAction = RoutingAction.Direct; configuration.ActiveProfile.DefaultProxyId = null;
            await Apply(); timer.Restart();
            var direct = await engine.ResolveDnsAsync(child.Id, childStart.FileName, query, deadline.Token);
            check(direct.Action == 1 && timer.ElapsedMilliseconds < 1000 && requests.All(request => !request.IsCompleted),
                "Direct DNS continues immediately while eight proxy DNS requests are stalled");
            timer.Restart();
            var stopped = await engine.DispatchAsync(RpcRequest.Create("Stop"), deadline.Token);
            foreach (var request in requests) { try { await request.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("Stalled DNS was not cancelled."); } catch (OperationCanceledException) { } }
            check(stopped.Success && timer.ElapsedMilliseconds < 2000, "Stopping capture cancels in-flight proxy DNS instead of waiting for the upstream timeout");
        }
        finally { foreach (var socket in sockets) socket.Dispose(); listener.Stop(); child.StandardInput.Close(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
    }
}
