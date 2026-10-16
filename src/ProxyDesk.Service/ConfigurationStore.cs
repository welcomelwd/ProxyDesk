using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ProxyDesk.Core;
using ProxyDesk.Platform.Windows;

namespace ProxyDesk.Service;

public sealed class ServiceOptions
{
    public string DataDirectory { get; }
    public string OwnerSid { get; }
    public ServiceOptions(string[] args)
    {
        var index = Array.IndexOf(args, "--data-dir");
        DataDirectory = index >= 0 ? Path.GetFullPath(args[index + 1]) :
            OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProxyDesk") : "/var/lib/proxydesk";
        Directory.CreateDirectory(DataDirectory);
        var ownerFile = Path.Combine(DataDirectory, "owner.sid");
        var ownerIndex = Array.IndexOf(args, "--owner-sid");
        OwnerSid = ownerIndex >= 0 ? args[ownerIndex + 1] : File.Exists(ownerFile) ? File.ReadAllText(ownerFile).Trim() :
            OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User!.Value : "";
        if (OperatingSystem.IsWindows()) _ = new SecurityIdentifier(OwnerSid);
    }
}

public sealed class ConfigurationStore
{
    private readonly ServiceOptions options;
    private readonly IPlatformIntegration? integration;
    private Dictionary<Guid, string> encryptedPasswords = [];
    public AppConfiguration Configuration { get; private set; }
    public ConfigurationStore(ServiceOptions options)
    {
        this.options = options;
        if (OperatingSystem.IsWindows()) integration = new WindowsIntegration();
        var configurationPath = Path.Combine(options.DataDirectory, "config.json");
        Configuration = File.Exists(configurationPath)
            ? JsonSerializer.Deserialize<AppConfiguration>(File.ReadAllText(configurationPath), JsonDefaults.Options)
                ?? throw new InvalidDataException("The configuration file is empty.")
            : AppConfiguration.CreateDefault();
        ConfigurationValidator.Validate(Configuration);
        var secretsPath = Path.Combine(options.DataDirectory, "secrets.json");
        if (File.Exists(secretsPath)) encryptedPasswords = JsonSerializer.Deserialize<Dictionary<Guid, string>>(File.ReadAllText(secretsPath), JsonDefaults.Options)!;
        foreach (var proxy in Configuration.Proxies) proxy.HasPassword = encryptedPasswords.ContainsKey(proxy.Id);
    }
    public string GetPassword(Guid id) => encryptedPasswords.TryGetValue(id, out var secret)
        ? Encoding.UTF8.GetString(integration!.UnprotectSecret(Convert.FromBase64String(secret))) : "";

    public void Save(AppConfiguration configuration, Dictionary<Guid, string> passwords)
    {
        ConfigurationValidator.Validate(configuration);
        foreach (var (id, password) in passwords)
        {
            if (!configuration.Proxies.Any(p => p.Id == id)) throw new InvalidDataException("A password must reference an existing proxy.");
            if (password.Length == 0) encryptedPasswords.Remove(id);
            else encryptedPasswords[id] = Convert.ToBase64String((integration ?? throw new PlatformNotSupportedException("Linux credential storage is not implemented yet.")).ProtectSecret(Encoding.UTF8.GetBytes(password)));
        }
        foreach (var id in encryptedPasswords.Keys.Where(id => configuration.Proxies.All(p => p.Id != id)).ToArray()) encryptedPasswords.Remove(id);
        foreach (var proxy in configuration.Proxies) proxy.HasPassword = encryptedPasswords.ContainsKey(proxy.Id);
        WriteAtomic("secrets.json", encryptedPasswords);
        WriteAtomic("config.json", configuration);
        Configuration = JsonDefaults.Clone(configuration);
    }
    private void WriteAtomic<T>(string name, T value)
    {
        var path = Path.Combine(options.DataDirectory, name);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, JsonDefaults.Options), new UTF8Encoding(false));
        File.Move(path + ".tmp", path, overwrite: true);
        if (name == "secrets.json" && File.Exists(Path.Combine(options.DataDirectory, "owner.sid")) && OperatingSystem.IsWindows())
            Installation.ProtectSecretFile(path);
    }
}
