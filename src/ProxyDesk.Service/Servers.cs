using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProxyDesk.Core;
using ProxyDesk.Platform.Windows;

namespace ProxyDesk.Service;

public sealed class ControlServer(EngineService engine, ServiceOptions options, ILogger<ControlServer> logger, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) { engine.Activity("Info", "Linux backend is unavailable in this Windows-first release."); return; }
        while (!token.IsCancellationRequested)
        {
            var pipe = PipeSecurityFactory.Create(ServiceClient.ControlPipe, options.OwnerSid);
            try { await pipe.WaitForConnectionAsync(token); _ = HandleAsync(pipe, token); }
            catch { await pipe.DisposeAsync(); throw; }
        }
    }
    [SupportedOSPlatform("windows")]
    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        await using (pipe)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var request = await FrameProtocol.ReadAsync<RpcRequest>(pipe, timeout.Token);
                if (request.Operation == "Shutdown")
                {
                    var shutdown = System.Text.Json.JsonSerializer.Deserialize<ShutdownRequest>(request.Payload, JsonDefaults.Options);
                    if (request.Version != 1 || shutdown?.ExpectedProcessId != Environment.ProcessId)
                    { await FrameProtocol.WriteAsync(pipe, RpcResponse.Fail("The service process changed. Refresh before restarting."), timeout.Token); return; }
                    await FrameProtocol.WriteAsync(pipe, RpcResponse.Ok(null), timeout.Token);
                    await pipe.FlushAsync(timeout.Token);
                    lifetime.StopApplication(); return;
                }
                if (request.Operation == "SubscribeEvents")
                {
                    while (pipe.IsConnected && !token.IsCancellationRequested)
                    {
                        await FrameProtocol.WriteAsync(pipe, RpcResponse.Ok(engine.State()), token);
                        await Task.Delay(750, token);
                    }
                    return;
                }
                await FrameProtocol.WriteAsync(pipe, await engine.DispatchAsync(request, timeout.Token), timeout.Token);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or System.Text.Json.JsonException or InvalidDataException)
            { if (!token.IsCancellationRequested) logger.LogDebug(error, "Control client disconnected or sent an invalid frame."); }
        }
    }
}

public sealed class DnsServer(EngineService engine, ServiceOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return;
        while (!token.IsCancellationRequested)
        {
            var pipe = PipeSecurityFactory.Create(ServiceClient.DnsPipe, options.OwnerSid);
            try { await pipe.WaitForConnectionAsync(token); _ = HandleAsync(pipe, token); }
            catch { await pipe.DisposeAsync(); throw; }
        }
    }
    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        await using (pipe)
        {
            try
            {
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid)) throw new IOException("Cannot identify DNS client process.");
                using var process = Process.GetProcessById((int)pid);
                var path = process.MainModule?.FileName ?? throw new IOException("Cannot identify DNS client executable.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var header = new byte[12]; await pipe.ReadExactlyAsync(header, timeout.Token);
                var version = BinaryPrimitives.ReadInt32LittleEndian(header);
                var operation = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
                var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
                if (version != 1 || length is < 0 or > 65535) throw new InvalidDataException("Invalid DNS IPC message.");
                int action; byte[] response;
                if (operation == 2) { engine.RegisterManaged((int)pid, path); action = 0; response = []; }
                else if (OperatingSystem.IsWindows() && operation == 5 && length == 12)
                {
                    var child = new byte[12]; await pipe.ReadExactlyAsync(child, timeout.Token);
                    engine.Processes.RegisterChild((int)pid, BinaryPrimitives.ReadInt32LittleEndian(child), BinaryPrimitives.ReadInt64LittleEndian(child.AsSpan(4)));
                    action = 0; response = [];
                }
                else if (operation == 3) { action = engine.DnsPolicy(path, (int)pid); response = []; }
                else if ((operation == 1 && length >= 12) || (operation == 4 && length >= 16))
                {
                    var payload = new byte[length]; await pipe.ReadExactlyAsync(payload, timeout.Token);
                    var query = operation == 4 ? payload[4..] : payload;
                    var queryOptions = operation == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : 0;
                    try { (action, response) = await engine.ResolveDnsAsync((int)pid, path, query, timeout.Token, queryOptions); }
                    catch (DnsCacheMissException) { action = 3; response = []; }
                }
                else throw new InvalidDataException("Unknown DNS operation.");
                var reply = new byte[8]; BinaryPrimitives.WriteInt32LittleEndian(reply, action);
                BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(4), response.Length);
                await pipe.WriteAsync(reply, timeout.Token); await pipe.WriteAsync(response, timeout.Token);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                engine.Activity("Error", "DNS client request failed: " + error.GetBaseException().Message);
                // A broken response is a hard DNS failure in the injected client; it never falls back to system DNS.
            }
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(nint pipe, out uint pid);
}

public sealed class AutomationWorker(EngineService engine) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        await engine.StartConfiguredAsync(token);
        if (!OperatingSystem.IsWindows()) return;
        var integration = new WindowsIntegration();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(token)) await engine.UpdateNetworkAsync(integration.GetCurrentSsid(), token);
    }
}

internal static class PipeSecurityFactory
{
    [SupportedOSPlatform("windows")]
    public static NamedPipeServerStream Create(string name, string ownerSid)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        var ownerRights = WindowsIdentity.GetCurrent().User!.Value == ownerSid ? PipeAccessRights.FullControl : PipeAccessRights.ReadWrite;
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(ownerSid), ownerRights, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 32, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 65536, 65536, security);
    }
}
