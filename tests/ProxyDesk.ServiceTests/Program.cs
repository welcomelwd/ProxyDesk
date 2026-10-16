using System.Text.Json;
using System.Diagnostics;
using ProxyDesk.Core;
using ProxyDesk.Service;
using ProxyDesk.Platform.Windows;

if (!OperatingSystem.IsWindows()) { Console.WriteLine("SERVICE_TEST_SKIP: Windows DPAPI is required."); return; }
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/service-tests", Guid.NewGuid().ToString("N")));
var options = new ServiceOptions(["--console", "--data-dir", root]);
var count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
Directory.CreateDirectory(root);
var damagedArchive = Path.Combine(root, "modified-driver.zip");
File.WriteAllBytes(damagedArchive, [0x50, 0x4b, 0, 0]);
try { WinDivertPackage.ReadVerifiedArchive(damagedArchive, Path.Combine(root, "invalid-output")); throw new Exception("A modified driver archive was accepted."); }
catch (InvalidDataException) { Check(!Directory.Exists(Path.Combine(root, "invalid-output")), "Modified driver packages are rejected before extraction"); }
try { InstalledWinDivert.VerifyLibrary([0x4d, 0x5a, 0, 0]); throw new Exception("An unverified installed library was accepted."); }
catch (InvalidDataException) { Check(true, "Installed driver reuse rejects an unknown library before loading native code"); }
var wrongArchitecture = new byte[128]; wrongArchitecture[0] = (byte)'M'; wrongArchitecture[1] = (byte)'Z';
BitConverter.GetBytes(64).CopyTo(wrongArchitecture, 60); BitConverter.GetBytes(0x4550).CopyTo(wrongArchitecture, 64);
BitConverter.GetBytes((ushort)0x14c).CopyTo(wrongArchitecture, 68);
try { WinDivertPackage.ValidateX64Image(wrongArchitecture); throw new Exception("An x86 driver was accepted."); }
catch (InvalidDataException) { Check(true, "Non-x64 driver images are rejected"); }
try { WinDivertPackage.VerifySignature(Path.Combine(AppContext.BaseDirectory, "ProxyDesk.ServiceTests.dll")); throw new Exception("An unsigned binary was accepted."); }
catch (InvalidDataException) { Check(true, "Unsigned binaries fail Windows trust verification"); }
var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../../dotnet.exe"));
WinDivertPackage.VerifySignature(dotnetHost);
Check(true, "Windows trust verification accepts the signed Microsoft SDK host");
var spacedRequest = Path.Combine(root, "setup operation", "request.json");
var scriptDirectory = Path.Combine(root, "script fixtures"); Directory.CreateDirectory(scriptDirectory);
var scriptPath = Path.Combine(scriptDirectory, "selected.ps1"); File.WriteAllText(scriptPath, "Write-Output 'fixture'");
var batchPath = Path.Combine(scriptDirectory, "selected.cmd"); File.WriteAllText(batchPath, "@exit /b 0");
Check(WindowsRuleTargets.Resolve(scriptPath).Kind == SelectorKind.ScriptPath, "Script file selection creates a script-path rule");
Check(WindowsRuleTargets.Resolve(scriptDirectory).Kind == SelectorKind.DirectoryGlob, "Folder selection preserves directory matching");
Check(WindowsRuleTargets.FindProcessScript(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "powershell.exe -NoProfile -File \"" + scriptPath + "\"") == scriptPath,
    "PowerShell script paths with spaces are identified");
Check(WindowsRuleTargets.FindProcessScript(@"C:\Windows\System32\cmd.exe", "cmd.exe /c \"\"" + batchPath + "\"\"") == batchPath,
    "Command scripts use Windows command-shell quoting");
Check(WindowsRuleTargets.FindProcessScript(@"C:\python.exe", "python.exe -c \"print('test')\" \"C:\\unrelated.py\"") is null,
    "Inline Python commands do not match arbitrary script arguments");
Check(WindowsRuleTargets.FindProcessScript(@"C:\node.exe", "node.exe --require C:\\preload.js C:\\entry.js") == @"C:\entry.js",
    "Node preloads are not mistaken for the entry script");
var shortcutPath = Path.Combine(root, "Script shortcut.lnk");
dynamic shortcutShell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)!;
try {
    dynamic shortcut = shortcutShell.CreateShortcut(shortcutPath);
    try { shortcut.TargetPath = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"; shortcut.Arguments = "-File \"" + scriptPath + "\""; shortcut.WorkingDirectory = scriptDirectory; shortcut.Save(); }
    finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut); }
} finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcutShell); }
var shortcutTarget = WindowsRuleTargets.Resolve(shortcutPath);
Check(shortcutTarget.Path == scriptPath && shortcutTarget.Kind == SelectorKind.ScriptPath && shortcutTarget.Name == "Script shortcut",
    "Shortcuts resolve their script target without executing the shortcut");
var catalog = new WindowsProcessCatalog(); catalog.Refresh();
Check(catalog.GetLineage(Environment.ProcessId, Environment.ProcessPath!)[0].ExecutablePath.Equals(Environment.ProcessPath!, StringComparison.OrdinalIgnoreCase),
    "The process catalog identifies the current process with WMI and a stable creation time");
try { catalog.RegisterChild(Environment.ProcessId, Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().ToFileTimeUtc()); throw new Exception("An unrelated child report was accepted."); }
catch (InvalidDataException) { Check(true, "A process cannot report an unrelated process as its child"); }
var elevation = WindowsNetworkSetup.CreateElevationStartInfo(@"C:\Program Files\ProxyDesk\ProxyDesk.Service.exe", spacedRequest);
Check(elevation.UseShellExecute && elevation.Verb == "runas" && elevation.WindowStyle == System.Diagnostics.ProcessWindowStyle.Hidden &&
    elevation.ArgumentList.SequenceEqual(new[] { "--prepare-network", spacedRequest }), "Administrator launch uses UAC and preserves the request path as a separate argument");
if (!WindowsIntegration.IsAdministrator)
{
    var guardDirectory = Path.Combine(root, "permission-guard"); Directory.CreateDirectory(guardDirectory);
    var guardStart = new System.Diagnostics.ProcessStartInfo(dotnetHost) { UseShellExecute = false, CreateNoWindow = true };
    guardStart.ArgumentList.Add(Path.Combine(repository, "src/ProxyDesk.Service/bin/Release/net10.0/ProxyDesk.Service.dll"));
    guardStart.ArgumentList.Add("--prepare-network"); guardStart.ArgumentList.Add(Path.Combine(guardDirectory, "request.json"));
    using var guard = System.Diagnostics.Process.Start(guardStart)!;
    await guard.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
    var guardResult = JsonSerializer.Deserialize<NetworkSetupResult>(await File.ReadAllTextAsync(Path.Combine(guardDirectory, "result.json")), JsonDefaults.Options)!;
    Check(guard.ExitCode == 1 && !guardResult.Success && guardResult.ServiceProcessId == 0, "The setup executable rejects an unelevated request before restarting a service or installing files");
}
foreach (var address in new[] { System.Net.IPAddress.Loopback, System.Net.IPAddress.IPv6Loopback })
{
    var listener = new System.Net.Sockets.TcpListener(address, 0); listener.Start();
    try
    {
        var upstream = new ProxyServer { Host = address.ToString(), Port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port };
        Check(ProxyDesk.Platform.Windows.WindowsIntegration.FindLocalProxyProcesses([upstream]).Contains(Environment.ProcessId),
            "Local upstream exclusion identifies the current listener: " + address);
    }
    finally { listener.Stop(); }
}
var proxy = new ProxyServer { Name = "Service fixture", Host = "127.0.0.1", Port = 65530, Username = "fixture-user" };
var configuration = AppConfiguration.CreateDefault(); configuration.Proxies.Add(proxy);
var rule = new ApplicationRule { Name = "Fixture program", Selector = new() { Kind = SelectorKind.ExecutableName, Value = "fixture.exe" }, ProxyId = proxy.Id };
configuration.ActiveProfile.Rules.Add(rule);
using (var engine = new EngineService(options))
{
    var runtime = engine.State().Runtime!;
    Check(runtime.ProcessId == Environment.ProcessId && runtime.DataDirectory == root && runtime.IsElevated == WindowsIntegration.IsAdministrator,
        "Service state reports the real process, data directory and administrator status");
    var setupRequest = WindowsNetworkSetup.CreateRequest(runtime, false, null);
    Check(setupRequest.PreviousProcessId == runtime.ProcessId && setupRequest.PreviousStartTimeUtcTicks == runtime.StartTimeUtcTicks && setupRequest.DataDirectory == root,
        "Administrator restart preserves the active data directory and process identity");
    var saved = await engine.DispatchAsync(RpcRequest.Create("ApplyConfiguration", new ConfigurationUpdate(configuration, new() { [proxy.Id] = "fixture-secret-unique" })), CancellationToken.None);
    Check(saved.Success && engine.State().Configuration.Proxies.Single().HasPassword, "Service applies configuration and password metadata");
    Check(!File.ReadAllText(Path.Combine(root, "config.json")).Contains("fixture-secret-unique") &&
        !File.ReadAllText(Path.Combine(root, "secrets.json")).Contains("fixture-secret-unique"), "Plaintext passwords are absent from persisted files");
    var invalid = JsonDefaults.Clone(configuration); invalid.Proxies.Clear();
    var rejected = await engine.DispatchAsync(RpcRequest.Create("ApplyConfiguration", new ConfigurationUpdate(invalid, [])), CancellationToken.None);
    Check(!rejected.Success && engine.State().Configuration.Proxies.Count == 1, "Rejected updates preserve the previous configuration");
    var profile = new Profile { Name = "Office" }; configuration.Profiles.Add(profile);
    configuration.Automations.Add(new() { Ssid = "FixtureWiFi", ProfileId = profile.Id });
    await engine.DispatchAsync(RpcRequest.Create("ApplyConfiguration", new ConfigurationUpdate(configuration, [])), CancellationToken.None);
    await engine.UpdateNetworkAsync("FixtureWiFi", CancellationToken.None);
    Check(engine.State().Configuration.ActiveProfileId == profile.Id, "SSID automation activates the mapped profile");
    await engine.UpdateNetworkAsync(null, CancellationToken.None);
    Check(engine.State().Configuration.ActiveProfileId == profile.Id, "Wi-Fi disconnection preserves the current profile");
    await engine.DispatchAsync(RpcRequest.Create("Recording", new ToggleRequest(false)), CancellationToken.None);
    Check(!engine.State().Recording, "Connection recording can be paused independently");
    var start = await engine.DispatchAsync(RpcRequest.Create("Start"), CancellationToken.None);
    Check(!start.Success && !engine.State().Running, "A missing driver cannot produce a running status");
    Check(!(await engine.DispatchAsync(new RpcRequest(2, "GetState", JsonSerializer.SerializeToElement<object?>(null)), CancellationToken.None)).Success, "The service rejects unsupported protocol versions");
}
using (var engine = new EngineService(options))
{
    Check(engine.State().Configuration.Proxies.Single().HasPassword, "Password metadata and configuration survive a service restart");
    var store = new ConfigurationStore(options);
    Check(store.GetPassword(proxy.Id) == "fixture-secret-unique", "DPAPI credentials can be recovered by the service");
}
Console.WriteLine($"SERVICE_TEST_PASS: {count} checks");
