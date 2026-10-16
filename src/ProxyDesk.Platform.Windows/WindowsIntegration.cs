using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using ProxyDesk.Core;

namespace ProxyDesk.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsIntegration : IPlatformIntegration
{
    public static bool IsAdministrator
    {
        get
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }
    public string ConfigurationDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProxyDesk");
    private static DataProtectionScope SecretScope => File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.mode")) ? DataProtectionScope.CurrentUser : DataProtectionScope.LocalMachine;
    public byte[] ProtectSecret(byte[] value) => ProtectedData.Protect(value, null, SecretScope);
    public byte[] UnprotectSecret(byte[] value) => ProtectedData.Unprotect(value, null, SecretScope);

    public void SetLaunchAtLogin(bool enabled)
    {
        // This method is called by the desktop process, so HKCU belongs to the interactive user.
        using var runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) runKey.SetValue("ProxyDesk", $"\"{Environment.ProcessPath}\" --tray");
        else runKey.DeleteValue("ProxyDesk", throwOnMissingValue: false);
    }

    public string? GetCurrentSsid()
    {
        if (WlanOpenHandle(2, 0, out _, out var handle) != 0) return null;
        try
        {
            if (WlanEnumInterfaces(handle, 0, out var interfaces) != 0) return null;
            try
            {
                var count = Marshal.ReadInt32(interfaces);
                for (var i = 0; i < count; i++)
                {
                    var entry = interfaces + 8 + i * 532;
                    if (Marshal.ReadInt32(entry, 528) != 1) continue;
                    var id = Marshal.PtrToStructure<Guid>(entry);
                    var result = WlanQueryInterface(handle, ref id, 7, 0, out _, out var data, out _);
                    if (result != 0) return null;
                    try
                    {
                        var length = Marshal.ReadInt32(data, 520);
                        var bytes = new byte[length]; Marshal.Copy(data + 524, bytes, 0, length);
                        return Encoding.UTF8.GetString(bytes);
                    }
                    finally { WlanFreeMemory(data); }
                }
                return null;
            }
            finally { WlanFreeMemory(interfaces); }
        }
        finally { WlanCloseHandle(handle, 0); }
    }

    public static HashSet<int> FindLocalProxyProcesses(IEnumerable<ProxyServer> proxies)
    {
        var ports = proxies.Where(p => p.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(p.Host, out var ip) && IPAddress.IsLoopback(ip)).Select(p => p.Port).ToHashSet();
        var result = new HashSet<int>();
        if (ports.Count == 0) return result;
        foreach (var family in new uint[] { 2, 23 })
        {
            uint length = 0; GetExtendedTcpTable(0, ref length, false, family, 3, 0);
            var buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                var status = GetExtendedTcpTable(buffer, ref length, false, family, 3, 0);
                if (status != 0) throw new System.ComponentModel.Win32Exception((int)status);
                var rowSize = family == 2 ? 24 : 56; var portOffset = family == 2 ? 8 : 20;
                for (var index = 0; index < Marshal.ReadInt32(buffer); index++)
                {
                    var row = buffer + 4 + index * rowSize;
                    var raw = Marshal.ReadInt32(row, portOffset);
                    var port = ((raw & 255) << 8) | ((raw >> 8) & 255);
                    if (ports.Contains(port)) result.Add(Marshal.ReadInt32(row, rowSize - 4));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return result;
    }

    public static int GetTcpClientPid(IPEndPoint endpoint)
    {
        uint length = 0; GetExtendedTcpTable(0, ref length, false, 2, 5, 0);
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            var status = GetExtendedTcpTable(buffer, ref length, false, 2, 5, 0);
            if (status != 0) throw new System.ComponentModel.Win32Exception((int)status);
            var address = BitConverter.ToInt32(endpoint.Address.GetAddressBytes());
            for (var index = 0; index < Marshal.ReadInt32(buffer); index++)
            {
                var row = buffer + 4 + index * 24;
                var rawPort = Marshal.ReadInt32(row, 8);
                var port = ((rawPort & 255) << 8) | ((rawPort >> 8) & 255);
                if (port == endpoint.Port && Marshal.ReadInt32(row, 4) == address) return Marshal.ReadInt32(row, 20);
            }
            return 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, nint reserved, out uint negotiated, out nint handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(nint handle, nint reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(nint handle, nint reserved, out nint list);
    [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(nint handle, ref Guid id, int opcode, nint reserved, out uint size, out nint data, out int type);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(nint memory);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(nint table, ref uint size, bool sorted, uint family, int tableClass, uint reserved);
}
