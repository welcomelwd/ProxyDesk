using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ProxyDesk.Core;

namespace ProxyDesk.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsProcessCatalog
{
    private sealed class Node(int pid, int parentPid, long generation, ApplicationProcess application)
    {
        public int Pid { get; } = pid;
        public int ParentPid { get; } = parentPid;
        public long Generation { get; } = generation;
        public ApplicationProcess Application { get; set; } = application;
        public Node? Parent { get; set; }
    }
    private readonly object gate = new();
    private Dictionary<int, Node> nodes = [];

    private static Node? Read(ManagementBaseObject item)
    {
        if (item["ExecutablePath"] is not string path || item["CreationDate"] is not string created) return null;
        var script = WindowsRuleTargets.FindProcessScript(path, item["CommandLine"] as string ?? "");
        return new(Convert.ToInt32(item["ProcessId"]), Convert.ToInt32(item["ParentProcessId"]),
            ManagementDateTimeConverter.ToDateTime(created).ToUniversalTime().Ticks / 10, new(path, script));
    }

    public void Refresh()
    {
        Dictionary<int, Node> observed;
        lock (gate) observed = new(nodes);
        using var query = new ManagementObjectSearcher("SELECT ProcessId,ParentProcessId,CreationDate,ExecutablePath,CommandLine FROM Win32_Process");
        using var results = query.Get();
        var current = new Dictionary<int, Node>();
        foreach (var item in results) { using (item) { var node = Read(item); if (node is not null) current[node.Pid] = node; } }
        lock (gate)
        {
            // Keep identities captured while the WMI snapshot was being enumerated.
            foreach (var pair in nodes)
                if ((!observed.TryGetValue(pair.Key, out var old) || !ReferenceEquals(old, pair.Value)) &&
                    (!current.TryGetValue(pair.Key, out var scanned) || scanned.Generation != pair.Value.Generation))
                    current[pair.Key] = pair.Value;
            foreach (var pair in current.ToArray())
                if (nodes.TryGetValue(pair.Key, out var existing) && existing.Generation == pair.Value.Generation)
                { existing.Application = pair.Value.Application; current[pair.Key] = existing; }
            foreach (var node in current.Values)
                if (node.Parent is null && node.ParentPid != node.Pid && current.TryGetValue(node.ParentPid, out var parent) && parent.Generation <= node.Generation)
                    node.Parent = parent;
            nodes = current;
        }
    }

    private static Node ReadOne(Process process, long generation)
    {
        // This path runs on the packet thread. WMI queries here can stall all captured traffic.
        var handle = process.SafeHandle;
        var path = new StringBuilder(32768); var length = path.Capacity;
        if (!QueryFullProcessImageName(handle, 0, path, ref length)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var status = NtQueryInformationProcess(handle, 0, out var information, Marshal.SizeOf<ProcessBasicInformation>(), out _);
        if (status != 0) throw new System.ComponentModel.Win32Exception((int)RtlNtStatusToDosError(status));
        var script = WindowsRuleTargets.FindProcessScript(path.ToString(), ReadCommandLine(handle));
        return new(process.Id, (int)information.ParentProcessId, generation, new(path.ToString(), script));
    }

    private Node? GetNode(int pid, HashSet<int> visited)
    {
        if (!visited.Add(pid)) return null;
        using var process = Process.GetProcessById(pid);
        var generation = process.StartTime.ToUniversalTime().Ticks / 10;
        if (nodes.TryGetValue(pid, out var existing) && existing.Generation == generation) return existing;
        var node = ReadOne(process, generation);
        nodes[pid] = node;
        if (node.ParentPid != pid)
        {
            try { node.Parent = GetNode(node.ParentPid, visited); }
            catch (ArgumentException) { }
            catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode is 5 or 87) { }
            if (node.Parent?.Generation > node.Generation) node.Parent = null;
        }
        return node;
    }

    public IReadOnlyList<ApplicationProcess> GetLineage(int pid, string path)
    {
        lock (gate)
        {
            Node? node;
            try { node = GetNode(pid, []); }
            catch (ArgumentException) { return [new(path)]; }
            catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode is 5 or 87) { return [new(path)]; }
            var lineage = new List<ApplicationProcess>(); var visited = new HashSet<Node>();
            for (; node is not null && visited.Add(node); node = node.Parent) lineage.Add(node.Application);
            return lineage.Count > 0 ? lineage : [new(path)];
        }
    }

    private static string ReadCommandLine(SafeProcessHandle process)
    {
        const int processCommandLineInformation = 60;
        NtQueryProcessBuffer(process, processCommandLineInformation, 0, 0, out var size);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var status = NtQueryProcessBuffer(process, processCommandLineInformation, buffer, size, out _);
            if (status != 0) throw new System.ComponentModel.Win32Exception((int)RtlNtStatusToDosError(status), $"Cannot read process command line: 0x{status:X8}.");
            var text = Marshal.PtrToStructure<UnicodeString>(buffer);
            return Marshal.PtrToStringUni(text.Buffer, text.Length / 2)!;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public nint Buffer; }
    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    private static extern int NtQueryProcessBuffer(SafeProcessHandle process, int informationClass, nint buffer, int length, out int returnedLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public nint ExitStatus, PebBaseAddress, AffinityMask, BasePriority, ProcessId, ParentProcessId;
    }
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass,
        out ProcessBasicInformation information, int length, out int returnedLength);
    [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int length);
}
