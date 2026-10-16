using Microsoft.Extensions.Hosting;
using ProxyDesk.Core;
using ProxyDesk.Platform.Windows;

namespace ProxyDesk.Service;

public sealed class ApplicationDnsWorker(EngineService engine, ServiceOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return;
        var attempted = new HashSet<(int Pid, DateTimeOffset StartedAt)>();
        AppConfiguration? previous = null;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(token))
        {
            var configuration = engine.DnsConfiguration;
            if (!ReferenceEquals(previous, configuration)) { attempted.Clear(); previous = configuration; }
            if (configuration is null) continue;
            IReadOnlyList<WindowsDnsTarget> targets;
            try { engine.Processes.Refresh(); targets = WindowsApplicationDns.FindTargets(configuration, options.OwnerSid, engine.Processes, engine.ManagedDnsProcesses); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.Management.ManagementException or System.Runtime.InteropServices.COMException)
            { engine.Activity("Warning", "Automatic DNS scan failed: " + error.Message); continue; }
            attempted.IntersectWith(targets.Select(target => (target.Pid, target.StartedAt)));
            foreach (var target in targets)
            {
                if (!attempted.Add((target.Pid, target.StartedAt))) continue;
                try { await WindowsApplicationDns.AttachAsync(target, token); }
                catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
                {
                    engine.Activity("Warning", $"Automatic DNS unavailable for {Path.GetFileName(target.Path)} ({target.Pid}): {error.Message}");
                }
            }
        }
    }
}
