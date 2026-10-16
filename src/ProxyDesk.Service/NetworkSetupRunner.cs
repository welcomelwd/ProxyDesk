using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text.Json;
using ProxyDesk.Core;
using ProxyDesk.Platform.Windows;

namespace ProxyDesk.Service;

[SupportedOSPlatform("windows")]
internal static class NetworkSetupRunner
{
    public static async Task<int> RunAsync(string requestPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(requestPath))!;
        NetworkSetupResult result;
        var newProcessId = 0;
        try
        {
            if (!WindowsIntegration.IsAdministrator) throw new UnauthorizedAccessException("需要 Windows 管理员授权才能安装驱动或重启网络后台。");
            var request = JsonSerializer.Deserialize<NetworkSetupRequest>(await File.ReadAllTextAsync(requestPath), JsonDefaults.Options)
                ?? throw new InvalidDataException("The network setup request is empty.");
            if (request.Version != 1) throw new InvalidDataException("Unsupported network setup request version.");
            _ = new System.Security.Principal.SecurityIdentifier(request.OwnerSid);
            var executable = Environment.ProcessPath!;
            if (!Path.GetFileName(executable).Equals("ProxyDesk.Service.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请使用发布目录中的 ProxyDesk.Service.exe 执行管理员操作。");
            var binaries = request.InstallDriver ? WinDivertPackage.ReadVerifiedArchive(
                request.ArchivePath ?? throw new InvalidDataException("The driver archive path is missing."), Path.Combine(directory, "verified")) : null;
            await StopPreviousAsync(request, executable);
            if (binaries is not null) InstallFiles(binaries, AppContext.BaseDirectory);
            if (request.RestartWindowsService)
            {
                using var service = new ServiceController("ProxyDesk");
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(25));
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(25));
            }
            else
            {
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
                start.ArgumentList.Add("--console"); start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(Path.GetFullPath(request.DataDirectory));
                start.ArgumentList.Add("--owner-sid"); start.ArgumentList.Add(request.OwnerSid);
                using var process = Process.Start(start) ?? throw new IOException("Windows did not start the administrator service process.");
                newProcessId = process.Id;
            }
            var state = await WaitForReadyAsync(newProcessId, request.PreviousProcessId);
            newProcessId = state.Runtime!.ProcessId;
            if (!state.Runtime.IsElevated) throw new UnauthorizedAccessException("新后台没有获得管理员权限。");
            if (!state.Capabilities.TrafficCapture) throw new InvalidOperationException("管理员后台已启动，但驱动不可用：" + state.Capabilities.Detail);
            await new ServiceClient().CallAsync("Start");
            var active = (await new ServiceClient().CallAsync("GetState")).Read<ServiceState>();
            if (!active.Running) throw new InvalidOperationException("后台未能启用代理，请查看活动日志。");
            result = new(true, "网络组件已就绪，管理员后台已启用代理。", newProcessId);
        }
        catch (Exception error)
        {
            result = new(false, error.GetBaseException().Message, newProcessId);
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(result, JsonDefaults.Options));
        return result.Success ? 0 : 1;
    }

    private static async Task StopPreviousAsync(NetworkSetupRequest request, string executable)
    {
        if (request.PreviousProcessId == 0) return;
        using var previous = Process.GetProcessById(request.PreviousProcessId);
        if (previous.StartTime.ToUniversalTime().Ticks != request.PreviousStartTimeUtcTicks ||
            !string.Equals(previous.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("原后台进程已经变化，请刷新界面后重试。");
        await new ServiceClient().CallAsync("Shutdown", new ShutdownRequest(request.PreviousProcessId));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await previous.WaitForExitAsync(timeout.Token);
    }

    private static async Task<ServiceState> WaitForReadyAsync(int expectedProcessId, int previousProcessId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try
            {
                var state = (await new ServiceClient().CallAsync("GetState", cancellationToken: timeout.Token)).Read<ServiceState>();
                if (state.Runtime is { } runtime && runtime.ProcessId != previousProcessId &&
                    (expectedProcessId == 0 || runtime.ProcessId == expectedProcessId)) return state;
            }
            catch (Exception error) when (error is IOException or System.TimeoutException) { }
            if (expectedProcessId != 0)
            {
                using var process = Process.GetProcessById(expectedProcessId);
                if (process.HasExited) throw new IOException("管理员后台在启动完成前退出。");
            }
            await Task.Delay(250, timeout.Token);
        }
    }

    private static void InstallFiles(Dictionary<string, byte[]> binaries, string directory)
    {
        var originals = new Dictionary<string, byte[]?>();
        var written = new List<string>();
        try
        {
            foreach (var name in WinDivertPackage.RequiredFiles)
            {
                var path = Path.Combine(directory, name);
                var previous = File.Exists(path) ? File.ReadAllBytes(path) : null;
                if (previous is not null && previous.AsSpan().SequenceEqual(binaries[name])) continue;
                originals[name] = previous; written.Add(name);
                File.WriteAllBytes(path, binaries[name]);
            }
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (var name in written)
            {
                try
                {
                    var path = Path.Combine(directory, name);
                    if (originals[name] is { } original) File.WriteAllBytes(path, original);
                    else if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception rollback) { errors.Add(rollback); }
            }
            if (errors.Count > 1) throw new AggregateException("Driver installation and rollback both failed.", errors);
            throw;
        }
    }
}
