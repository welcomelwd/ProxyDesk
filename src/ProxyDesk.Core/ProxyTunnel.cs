using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace ProxyDesk.Core;

public static class ProxyTunnel
{
    public static async Task<Stream> OpenAsync(ProxyServer proxy, string password, string destination,
        int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(proxy.Host, proxy.Port, cancellationToken);
            Stream stream = new NetworkStream(socket, ownsSocket: true);
            if (proxy.Kind == ProxyKind.Https)
            {
                var tls = new SslStream(stream, leaveInnerStreamOpen: false);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = proxy.Host }, cancellationToken);
                stream = tls;
            }
            try
            {
                if (proxy.Kind == ProxyKind.Socks5)
                    await SocksConnectAsync(stream, proxy.Username, password, destination, port, cancellationToken);
                else
                {
                    var authority = destination.Contains(':') ? $"[{destination}]:{port}" : $"{destination}:{port}";
                    var authorization = string.IsNullOrEmpty(proxy.Username) ? "" :
                        "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(proxy.Username + ":" + password)) + "\r\n";
                    var request = $"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n{authorization}\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken);
                    var header = await ReadHttpHeaderAsync(stream, cancellationToken);
                    var firstLine = header.Split("\r\n")[0];
                    if (!firstLine.StartsWith("HTTP/1.", StringComparison.Ordinal) || firstLine.Split(' ')[1] != "200")
                        throw new IOException("Proxy CONNECT failed: " + firstLine);
                }
                return stream;
            }
            catch { await stream.DisposeAsync(); throw; }
        }
        catch { socket.Dispose(); throw; }
    }

    public static async Task SocksAuthenticateAsync(Stream stream, string username, string password, CancellationToken token)
    {
        await stream.WriteAsync(string.IsNullOrEmpty(username) ? new byte[] { 5, 1, 0 } : new byte[] { 5, 2, 0, 2 }, token);
        var response = new byte[2];
        await stream.ReadExactlyAsync(response, token);
        if (response[0] != 5 || response[1] is not (0 or 2)) throw new IOException("SOCKS5 authentication method rejected.");
        if (response[1] == 2)
        {
            var user = Encoding.UTF8.GetBytes(username);
            var secret = Encoding.UTF8.GetBytes(password);
            if (user.Length > 255 || secret.Length > 255) throw new InvalidDataException("SOCKS5 credentials exceed 255 bytes.");
            await stream.WriteAsync(new byte[] { 1, (byte)user.Length }.Concat(user).Concat(new byte[] { (byte)secret.Length }).Concat(secret).ToArray(), token);
            await stream.ReadExactlyAsync(response, token);
            if (response[1] != 0) throw new IOException("SOCKS5 authentication failed.");
        }
    }

    private static async Task SocksConnectAsync(Stream stream, string username, string password, string host, int port, CancellationToken token)
    {
        await SocksAuthenticateAsync(stream, username, password, token);
        byte[] address;
        if (IPAddress.TryParse(host, out var ip)) address = new[] { (byte)(ip.AddressFamily == AddressFamily.InterNetwork ? 1 : 4) }.Concat(ip.GetAddressBytes()).ToArray();
        else
        {
            var domain = Encoding.ASCII.GetBytes(new System.Globalization.IdnMapping().GetAscii(host));
            if (domain.Length > 255) throw new InvalidDataException("SOCKS5 destination name is too long.");
            address = new byte[] { 3, (byte)domain.Length }.Concat(domain).ToArray();
        }
        await stream.WriteAsync(new byte[] { 5, 1, 0 }.Concat(address).Concat(new[] { (byte)(port >> 8), (byte)port }).ToArray(), token);
        var response = new byte[4];
        await stream.ReadExactlyAsync(response, token);
        if (response[0] != 5 || response[1] != 0) throw new IOException($"SOCKS5 CONNECT failed: {response[1]}.");
        var length = response[3] switch { 1 => 4, 4 => 16, 3 => await ReadByteAsync(stream, token), _ => throw new IOException("Invalid SOCKS5 address type.") };
        await stream.ReadExactlyAsync(new byte[length + 2], token);
    }

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken token)
    {
        var value = new byte[1]; await stream.ReadExactlyAsync(value, token); return value[0];
    }

    public static async Task<string> ReadHttpHeaderAsync(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 32768)
        {
            await stream.ReadExactlyAsync(one, token); bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10)
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new InvalidDataException("HTTP header exceeds 32 KiB.");
    }

    public static HttpClient CreateHttpClient(ProxyServer proxy, string password, string? bootstrapIp = null)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectCallback = async (context, token) => await OpenAsync(proxy, password,
                bootstrapIp ?? context.DnsEndPoint.Host, context.DnsEndPoint.Port, token)
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    public static async Task<ProxyTestResult> TestAsync(ProxyServer proxy, string password, string url, CancellationToken token)
    {
        try
        {
            var timer = Stopwatch.StartNew();
            using var client = CreateHttpClient(proxy, password);
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            return new(proxy.Id, true, timer.Elapsed.TotalMilliseconds, null, $"HTTP {(int)response.StatusCode}; TCP tunnel and authentication succeeded.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        {
            return new(proxy.Id, false, null, null, error.GetBaseException().Message);
        }
    }
}
