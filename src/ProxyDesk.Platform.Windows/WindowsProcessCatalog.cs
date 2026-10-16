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
        public bool LaunchInjected { get; set; }
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

    private Node? ReadOne(int pid)
    {
        using var query = new ManagementObjectSearcher($"SELECT ProcessId,ParentProcessId,CreationDate,ExecutablePath,CommandLine FROM Win32_Process WHERE ProcessId={pid}");
        using var results = query.Get();
        foreach (var item in results) using (item) return Read(item);
        return null;
    }

    private Node? GetNode(int pid, HashSet<int> visited)
    {
        if (!visited.Add(pid)) return null;
        using var process = Process.GetProcessById(pid);
        var generation = process.StartTime.ToUniversalTime().Ticks / 10;
        if (nodes.TryGetValue(pid, out var existing) && existing.Generation == generation) return existing;
        var node = ReadOne(pid);
        if (node is null) return null;
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

    public void RegisterChild(int parentPid, int childPid, long creationFileTime)
    {
        using var process = Process.GetProcessById(childPid);
        if (process.StartTime.ToUniversalTime().ToFileTimeUtc() != creationFileTime) throw new InvalidDataException("Child process identity changed.");
        var status = NtQueryInformationProcess(process.SafeHandle, 0, out var information, Marshal.SizeOf<ProcessBasicInformation>(), out _);
        if (status != 0) throw new InvalidOperationException($"Cannot read child process identity: 0x{status:X8}.");
        if (information.ParentProcessId != parentPid) throw new InvalidDataException("The reported process is not a child of the caller.");
        var path = new StringBuilder(32768); var length = path.Capacity;
        if (!QueryFullProcessImageName(process.SafeHandle, 0, path, ref length)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        lock (gate)
        {
            // Suspended children are not yet visible through WMI. Capture their identity before resuming them.
            var child = new Node(childPid, parentPid, process.StartTime.ToUniversalTime().Ticks / 10, new(path.ToString()));
            var parent = GetNode(parentPid, []) ?? throw new InvalidDataException("Cannot identify the parent process.");
            if (parent.Generation > child.Generation) throw new InvalidDataException("The reported parent process was created after the child.");
            child.Parent = parent;
            child.LaunchInjected = true;
            nodes[childPid] = child;
        }
    }
    public bool IsLaunchInjected(int pid) { lock (gate) return nodes.TryGetValue(pid, out var node) && node.LaunchInjected; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public nint ExitStatus, PebBaseAddress, AffinityMask, BasePriority, ProcessId, ParentProcessId;
    }
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass,
        out ProcessBasicInformation information, int length, out int returnedLength);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int length);
}
