using System.IO.Pipes;
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
