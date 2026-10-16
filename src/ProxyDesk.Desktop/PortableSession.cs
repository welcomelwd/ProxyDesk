using System.Runtime.Versioning;
using System.Security.Principal;
using ProxyDesk.Core;
using ProxyDesk.Platform.Windows;

namespace ProxyDesk.Desktop;

internal static class PortableSession
{
    public static bool Enabled => OperatingSystem.IsWindows() && File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.mode"));
    public static string DataDirectory => Path.Combine(AppContext.BaseDirectory, "Data");
    public static bool Owns(ServiceRuntime? runtime) => Enabled && runtime is { IsWindowsService: false } &&
        string.Equals(Path.GetFullPath(runtime.DataDirectory), Path.GetFullPath(DataDirectory), StringComparison.OrdinalIgnoreCase);

    [SupportedOSPlatform("windows")]
    public static NetworkSetupRequest CreateRequest(ServiceRuntime? runtime)
    {
        if (runtime is not null && !Owns(runtime)) throw new InvalidOperationException("已有其他后台运行，请从原窗口退出后再启动便携后台。");
        using var identity = WindowsIdentity.GetCurrent();
        return new(1, false, null, DataDirectory, identity.User!.Value, runtime?.ProcessId ?? 0, runtime?.StartTimeUtcTicks ?? 0, false);
    }

    public static string CreateOperationDirectory()
    {
        var directory = Path.Combine(DataDirectory, "Setup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); return directory;
    }
}
