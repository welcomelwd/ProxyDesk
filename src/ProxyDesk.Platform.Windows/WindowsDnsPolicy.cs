namespace ProxyDesk.Platform.Windows;

public static class WindowsDnsPolicy
{
    public static string? SystemDnsReason(int pid, string path)
    {
        if (pid is 0 or 4 || !Path.IsPathFullyQualified(path)) return "unattributed-owner";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (path.Equals(Path.Combine(windows, "System32", "svchost.exe"), StringComparison.OrdinalIgnoreCase) ||
            path.Equals(Path.Combine(windows, "SysWOW64", "svchost.exe"), StringComparison.OrdinalIgnoreCase))
            return "shared-system-resolver-original-application-unknown";
        return null;
    }
}
