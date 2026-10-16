using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ProxyDesk.Core;

namespace ProxyDesk.Platform.Windows;

public sealed record WindowsDnsTarget(int Pid, string Path, DateTimeOffset StartedAt, bool Is32Bit);

[SupportedOSPlatform("windows")]
public static class WindowsApplicationDns
{
    public static int AttachmentMode(AppConfiguration configuration, RoutingDecision decision)
    {
        // Child IPC modes: DNS interception, no attachment, or process tracking only.
        if (decision.Action == RoutingAction.Block || decision.Action == RoutingAction.Proxy && decision.StrictDns) return 0;
        return configuration.ActiveProfile.Rules.Any(rule => rule.Id == decision.RuleId && rule.IncludeDescendants) ? 3 : 1;
    }

    public static IReadOnlyList<WindowsDnsTarget> FindTargets(AppConfiguration configuration, string ownerSid,
        WindowsProcessCatalog processes, IReadOnlyList<ManagedProcess> attached)
    {
        var targets = new List<WindowsDnsTarget>();
        var upstream = WindowsIntegration.FindLocalProxyProcesses(configuration.Proxies);
        var path = new StringBuilder(32768);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId || upstream.Contains(process.Id)) continue;
                using var handle = OpenProcess(0x1000, false, process.Id);
                if (handle.IsInvalid) continue;
                var length = path.Capacity;
                if (!QueryFullProcessImageName(handle, 0, path, ref length)) continue;
                var executable = path.ToString();
                if (!OpenProcessToken(handle, 0x0008, out var token)) continue;
                using (token)
                using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                    if (identity.User?.Value != ownerSid) continue;
                var decision = RuleEngine.Evaluate(configuration, processes.GetLineage(process.Id, executable), AppPlatform.Windows);
                if (!GetProcessTimes(handle, out var created, out _, out _, out _) || !IsWow64Process2(handle, out var machine, out var nativeMachine)) continue;
                var startedAt = DateTimeOffset.FromFileTime(created);
                // Reconfigure existing hooks when a rule is disabled or becomes direct.
                if (AttachmentMode(configuration, decision) == 1 && !attached.Any(item => item.Pid == process.Id && item.StartedAt == startedAt)) continue;
                if (nativeMachine != 0x8664 || machine is not (0 or 0x014c)) continue;
                targets.Add(new(process.Id, executable, startedAt, machine == 0x014c));
            }
        }
        return targets;
    }

    public static async Task AttachAsync(WindowsDnsTarget target, CancellationToken token)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, target.Is32Bit ? "ProxyDesk.Injector32.exe" : "ProxyDesk.Injector64.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add(target.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(target.StartedAt.ToFileTime().ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0) throw new IOException(error.Trim());
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(SafeProcessHandle process, out ushort machine, out ushort nativeMachine);
}
