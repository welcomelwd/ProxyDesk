using Microsoft.Extensions.Hosting;

namespace ProxyDesk.Service;

public sealed class ProcessCatalogWorker(EngineService engine) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(token))
        {
            if (engine.DnsConfiguration is null) continue;
            try { engine.Processes.Refresh(); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.Management.ManagementException or System.Runtime.InteropServices.COMException)
            { engine.Activity("Warning", "Process catalog refresh failed: " + error.Message); }
        }
    }
}
