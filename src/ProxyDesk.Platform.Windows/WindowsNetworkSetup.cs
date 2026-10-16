using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using ProxyDesk.Core;

namespace ProxyDesk.Platform.Windows;

public sealed record NetworkSetupRequest(int Version, bool InstallDriver, string? ArchivePath, string DataDirectory,
    string OwnerSid, int PreviousProcessId, long PreviousStartTimeUtcTicks, bool RestartWindowsService);
public sealed record NetworkSetupResult(bool Success, string Message, int ServiceProcessId = 0);

[SupportedOSPlatform("windows")]
public static class WindowsNetworkSetup
{
    public static string CreateOperationDirectory()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProxyDesk", "Setup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    public static NetworkSetupRequest CreateRequest(ServiceRuntime? runtime, bool installDriver, string? archivePath)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var serviceKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ProxyDesk");
        var registeredHere = serviceKey?.GetValue("ImagePath") is string imagePath &&
            string.Equals(Environment.ExpandEnvironmentVariables(imagePath).Trim('"'), Path.Combine(AppContext.BaseDirectory, "ProxyDesk.Service.exe"), StringComparison.OrdinalIgnoreCase);
        var restartService = runtime?.IsWindowsService ?? registeredHere;
        return new(1, installDriver, archivePath,
            runtime?.DataDirectory ?? (restartService ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProxyDesk") :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProxyDesk", "Preview")),
            identity.User!.Value, runtime?.ProcessId ?? 0, runtime?.StartTimeUtcTicks ?? 0, restartService);
    }
    public static ProcessStartInfo CreateElevationStartInfo(string executable, string requestPath)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--prepare-network"); start.ArgumentList.Add(Path.GetFullPath(requestPath));
        return start;
    }
    public static async Task<NetworkSetupResult> ElevateAsync(NetworkSetupRequest request, string directory, string serviceExecutable, CancellationToken token)
    {
        if (!File.Exists(serviceExecutable)) throw new FileNotFoundException("The packaged service executable is missing.", serviceExecutable);
        var requestPath = Path.Combine(directory, "request.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, JsonDefaults.Options), token);
        Process helper;
        try
        {
            helper = await Task.Run(() => Process.Start(CreateElevationStartInfo(serviceExecutable, requestPath))
                ?? throw new IOException("Windows did not start the elevated helper."), token);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        { return new(false, "已取消管理员授权，原后台保持运行。"); }
        using (helper)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(90));
            await helper.WaitForExitAsync(timeout.Token);
            var resultPath = Path.Combine(directory, "result.json");
            if (!File.Exists(resultPath)) return new(false, $"管理员操作未返回结果，退出码：{helper.ExitCode}。请查看 Windows 的启动或安全提示。");
            return JsonSerializer.Deserialize<NetworkSetupResult>(await File.ReadAllTextAsync(resultPath, token), JsonDefaults.Options)
                ?? throw new InvalidDataException("The administrator helper returned an empty result.");
        }
    }
}
