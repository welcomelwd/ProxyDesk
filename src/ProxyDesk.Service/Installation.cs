using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace ProxyDesk.Service;

internal static class Installation
{
    [SupportedOSPlatform("windows")]
    public static void Initialize(string directory, string ownerSid)
    {
        if (!ProxyDesk.Platform.Windows.WindowsIntegration.IsAdministrator) throw new UnauthorizedAccessException("Administrator authorization is required to install the Windows service.");
        var owner = new SecurityIdentifier(ownerSid.Trim());
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        Directory.CreateDirectory(directory);
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
        File.WriteAllText(Path.Combine(directory, "owner.sid"), owner.Value);
    }

    [SupportedOSPlatform("windows")]
    public static void RegisterService()
    {
        if (!string.Equals(Path.GetFileName(Environment.ProcessPath), "ProxyDesk.Service.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use the packaged ProxyDesk.Service.exe to register the service.");
        var applicationSecurity = new DirectorySecurity(); applicationSecurity.SetAccessRuleProtection(true, false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        applicationSecurity.SetOwner(administrators);
        applicationSecurity.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        applicationSecurity.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        applicationSecurity.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(AppContext.BaseDirectory).SetAccessControl(applicationSecurity);
        var manager = OpenSCManager(null, null, 0x0002 | 0x0001);
        if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var executable = "\"" + Environment.ProcessPath + "\"";
            var service = CreateService(manager, "ProxyDesk", "ProxyDesk Background Service", 0xF01FF, 0x10, 2, 1, executable, null, 0, null, null, null);
            if (service == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 1073) throw new Win32Exception(error);
                service = OpenService(manager, "ProxyDesk", 0xF01FF);
                if (service == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!ChangeServiceConfig(service, 0xFFFFFFFF, 2, 0xFFFFFFFF, executable, null, 0, null, null, null, null))
                { var failure = Marshal.GetLastWin32Error(); CloseServiceHandle(service); throw new Win32Exception(failure); }
            }
            CloseServiceHandle(service);
        }
        finally { CloseServiceHandle(manager); }
    }

    [SupportedOSPlatform("windows")]
    public static void ProtectSecretFile(string path)
    {
        var security = new FileSecurity(); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateService(nint manager, string name, string displayName, uint access, uint type, uint start, uint errorControl, string binaryPath, string? group, nint tag, string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfig(nint service, uint type, uint start, uint errorControl, string binaryPath, string? group, nint tag, string? dependencies, string? account, string? password, string? displayName);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(nint handle);
}
