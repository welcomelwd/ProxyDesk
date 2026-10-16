using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using ProxyDesk.Platform.Windows;
using Microsoft.Win32;

namespace ProxyDesk.Desktop.Windows;

internal static class InstalledApplications
{
    internal sealed record Application(string Name, string Directory, string Description);
    internal sealed record StartMenuApplication(string Name, string Path, string Target);

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<StartMenuApplication> ReadStartMenu()
    {
        var applications = new Dictionary<string, StartMenuApplication>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
        {
            var directory = Environment.GetFolderPath(folder);
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                try
                {
                    var target = WindowsRuleTargets.Resolve(path);
                    if (target.Kind != ProxyDesk.Core.SelectorKind.DirectoryGlob)
                        applications[target.Path] = new(System.IO.Path.GetFileNameWithoutExtension(path), path, target.Path);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or System.Runtime.InteropServices.COMException or ArgumentException) { }
            }
        }
        AddPackagedApplications(applications);
        return applications.Values.OrderBy(application => application.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static void AddPackagedApplications(Dictionary<string, StartMenuApplication> applications)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true)!)!;
        try
        {
            dynamic folder = shell.Namespace("shell:AppsFolder");
            try
            {
                dynamic items = folder.Items();
                try
                {
                    for (var index = 0; index < (int)items.Count; index++)
                    {
                        dynamic item = items.Item(index);
                        try
                        {
                            string appId = item.Path;
                            var separator = appId.IndexOf('!');
                            if (separator < 0 || item.ExtendedProperty("System.AppUserModel.PackageInstallPath") is not string directory) continue;
                            var manifestPath = System.IO.Path.Combine(directory, "AppxManifest.xml");
                            if (!File.Exists(manifestPath)) continue;
                            var manifest = XDocument.Load(manifestPath);
                            var id = appId[(separator + 1)..];
                            var application = manifest.Descendants().FirstOrDefault(element => element.Name.LocalName == "Application" &&
                                string.Equals((string?)element.Attribute("Id"), id, StringComparison.OrdinalIgnoreCase));
                            if (application?.Attribute("Executable") is not { } executable) continue;
                            var path = System.IO.Path.GetFullPath(executable.Value, directory);
                            if (File.Exists(path)) applications[path] = new((string)item.Name, path, path);
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Xml.XmlException or COMException or ArgumentException) { }
                        finally { Marshal.FinalReleaseComObject(item); }
                    }
                }
                finally { Marshal.FinalReleaseComObject(items); }
            }
            finally { Marshal.FinalReleaseComObject(folder); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<Application> Read()
    {
        var applications = new Dictionary<string, Application>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) continue;
            foreach (var keyName in uninstall.GetSubKeyNames())
            {
                using var key = uninstall.OpenSubKey(keyName);
                if (key?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                var directory = (key.GetValue("InstallLocation") as string)?.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) continue;
                var description = string.Join(" · ", new[] { key.GetValue("Publisher") as string, key.GetValue("DisplayVersion") as string }.Where(value => !string.IsNullOrWhiteSpace(value)));
                applications[directory] = new(name, directory, description.Length > 0 ? description : directory);
            }
        }
        return applications.Values.OrderBy(application => application.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
