using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Principal;

namespace ProxyDesk.Core;

public sealed class ServiceClient
{
    public const string ControlPipe = "ProxyDesk.Control.v1";
    public const string DnsPipe = "ProxyDesk.Dns.v1";
    public async Task<RpcResponse> CallAsync(string operation, object? payload = null, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(operation == "TestProxy" ? TimeSpan.FromSeconds(25) : TimeSpan.FromSeconds(5));
        await using var stream = await ConnectAsync(timeout.Token);
        await FrameProtocol.WriteAsync(stream, RpcRequest.Create(operation, payload), timeout.Token);
        var response = await FrameProtocol.ReadAsync<RpcResponse>(stream, timeout.Token);
        if (!response.Success) throw new InvalidOperationException(response.Error);
        return response;
    }
    public async Task<Stream> ConnectAsync(CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", ControlPipe, PipeDirection.InOut, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Impersonation);
            try { await pipe.ConnectAsync(1000, token); return pipe; }
            catch { pipe.Dispose(); throw; }
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { await socket.ConnectAsync(new UnixDomainSocketEndPoint("/run/proxydesk/control.sock"), token); return new NetworkStream(socket, true); }
        catch { socket.Dispose(); throw; }
    }
}
