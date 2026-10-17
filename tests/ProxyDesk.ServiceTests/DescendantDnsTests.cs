using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using ProxyDesk.Core;
using ProxyDesk.Platform.Windows;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static class DescendantDnsTests
{
    public static async Task RunAsync(string repository, Action<bool, string> check)
    {
        foreach (var architecture in new[] { "64", "32" }) await RunArchitectureAsync(repository, architecture, check);
    }
    private static async Task RunArchitectureAsync(string repository, string architecture, Action<bool, string> Check)
    {
        var root = Path.Combine(repository, "artifacts", "descendant-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var native = Path.Combine(repository, "artifacts", "native-tests");
        var parentPath = Path.Combine(root, "Parent.exe");
        var helperPath = Path.Combine(root, "Helper.exe");
        File.Copy(Path.Combine(native, "ProxyDesk.DescendantProbe" + architecture + ".exe"), parentPath);
        File.Copy(parentPath, helperPath);
        var launcher = Path.Combine(native, "ProxyDesk.Launcher.exe");
        using var bypass = new EventWaitHandle(false, EventResetMode.ManualReset, @"Global\ProxyDesk.Dns.Tests.Bypass.v1");
        bypass.Reset();
        using var stop = new CancellationTokenSource();
        var catalog = new WindowsProcessCatalog();
        var broker = new Broker(catalog);
        var server = broker.RunAsync(stop.Token);
        async Task WaitFile(string path)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(path)) await Task.Delay(10, timeout.Token);
        }
        async Task<(bool Loaded, int Status)> Run(string name, bool inherited, bool broken, RoutingAction expected, Action? reconfigure = null, string? script = null)
        {
            var output = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N"));
            var start = new ProcessStartInfo(inherited ? launcher : helperPath)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (inherited) { start.ArgumentList.Add(parentPath); start.ArgumentList.Add($"--parent \"{helperPath}\" \"{output}\"" + (script is null ? "" : $" \"{script}\"")); }
            else { start.ArgumentList.Add("--child"); start.ArgumentList.Add(output); }
            using var launch = Process.Start(start)!;
            await WaitFile(output + ".ready");
            var pid = int.Parse(await File.ReadAllTextAsync(output + ".ready"));
            using var child = Process.GetProcessById(pid);
            _ = child.Handle;
            var lineage = catalog.GetLineage(pid, helperPath);
            var decision = RuleEngine.Evaluate(broker.Configuration, lineage, AppPlatform.Windows);
            Check(decision.Action == expected, name + " routing action is " + expected);
            Check(lineage.Any(p => p.ExecutablePath.Equals(parentPath, StringComparison.OrdinalIgnoreCase)) == inherited,
                name + " has only its actual launching ancestry");
            if (reconfigure is not null)
            {
                reconfigure();
                var startInjector = new ProcessStartInfo(Path.Combine(native, "ProxyDesk.Injector" + architecture + ".exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                startInjector.ArgumentList.Add(pid.ToString()); startInjector.ArgumentList.Add(child.StartTime.ToUniversalTime().ToFileTimeUtc().ToString());
                using var injector = Process.Start(startInjector)!;
                var error = await injector.StandardError.ReadToEndAsync(); await injector.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Check(injector.ExitCode == 0, name + " reconfigures the running DNS component: " + error);
            }
            if (broken) broker.BrokenPid = pid;
            await File.WriteAllTextAsync(output + ".go", "go");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await launch.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Check(child.ExitCode == 0 && launch.ExitCode == 0, name + " exits normally");
            var values = (await File.ReadAllLinesAsync(output + ".result")).Select(line => line.Split('=')).ToDictionary(x => x[0], x => int.Parse(x[1]));
            Check(values["production_loaded"] == 0, name + " is isolated from the installed DNS component");
            Console.WriteLine($"OBSERVATION: {name} PID={pid} action={decision.Action} injected={values["loaded"]} dns_status={values["dns"]}");
            broker.BrokenPid = 0;
            return (values["loaded"] == 1, values["dns"]);
        }
        try
        {
            var configuration = AppConfiguration.CreateDefault();
            var proxy = new ProxyServer { Name = "Unused audit proxy", Host = "127.0.0.1", Port = 65530 };
            configuration.Proxies.Add(proxy);
            var parentRule = new ApplicationRule
            {
                Name = "Audit parent",
                Selector = new() { Kind = SelectorKind.FullPath, Value = parentPath },
                Action = RoutingAction.Proxy,
                ProxyId = proxy.Id,
                IncludeDescendants = false
            };
            configuration.ActiveProfile.Rules.Add(parentRule);
            broker.Configuration = configuration;
            var baseline = await Run("baseline", false, false, RoutingAction.Direct);
            Check(!baseline.Loaded, "Unrelated application starts without the DNS component");
            var disabled = await Run("inheritance-disabled", true, false, RoutingAction.Direct);
            Check(!disabled.Loaded && disabled.Status == baseline.Status, "Disabling descendants leaves the child unhooked with system DNS");
            parentRule.IncludeDescendants = true;
            var childRule = new ApplicationRule
            {
                Name = "Direct child override",
                Selector = new() { Kind = SelectorKind.FullPath, Value = helperPath },
                Action = RoutingAction.Direct,
                IncludeDescendants = false
            };
            configuration.ActiveProfile.Rules.Insert(0, childRule);
            var exempt = await Run("direct-override-broker-error", true, true, RoutingAction.Direct);
            Check(!exempt.Loaded && exempt.Status == baseline.Status,
                "An explicitly direct child does not depend on the DNS broker");
            childRule.IncludeDescendants = true;
            var tracking = await Run("direct-tracking-broker-error", true, true, RoutingAction.Direct);
            Check(tracking.Loaded && tracking.Status == baseline.Status, "Tracking-only descendants use system DNS even when the broker fails");
            var promoted = await Run("tracking-promoted-to-proxy", true, false, RoutingAction.Direct,
                () => configuration.ActiveProfile.Rules.Remove(childRule));
            Check(promoted.Loaded && promoted.Status == 9005, "A running tracking-only process can enable proxy DNS after a rule change");
            var unrelated = await Run("same-exe-after-child-exit", false, false, RoutingAction.Direct);
            Check(!unrelated.Loaded && unrelated.Status == baseline.Status,
                "The same executable launched independently after child exit has no inherited rule or DNS hook");
            var allowed = await Run("inheritance-enabled", true, false, RoutingAction.Proxy);
            Check(allowed.Loaded && allowed.Status == 9005, "A real descendant keeps its parent rule after the launcher exits");
            var downgraded = await Run("proxy-changed-to-direct", true, true, RoutingAction.Proxy,
                () => parentRule.IncludeDescendants = false);
            Check(downgraded.Loaded && downgraded.Status == baseline.Status, "An already injected process restores system DNS after inheritance is disabled");
            parentRule.IncludeDescendants = true;
            helperPath = Path.Combine(root, "python.exe"); File.Copy(parentPath, helperPath);
            var scriptPath = Path.Combine(root, "direct child.py"); File.WriteAllText(scriptPath, "");
            childRule.Selector = new() { Kind = SelectorKind.ScriptPath, Value = scriptPath }; childRule.IncludeDescendants = false;
            configuration.ActiveProfile.Rules.Insert(0, childRule);
            var scriptChild = await Run("script-direct-override", true, true, RoutingAction.Direct, script: scriptPath);
            Check(!scriptChild.Loaded && scriptChild.Status == baseline.Status, "Script rules are honored before a suspended child starts");
            configuration.ActiveProfile.Rules.Remove(childRule);
            File.Copy(Path.Combine(native, "ProxyDesk.DescendantProbe" + (architecture == "64" ? "32" : "64") + ".exe"), helperPath, true);
            var crossBit = await Run("cross-bitness-child", true, false, RoutingAction.Proxy);
            Check(crossBit.Loaded && crossBit.Status == 9005, "Child DNS policy survives cross-bitness injection");
            Check(broker.Errors.IsEmpty, "Child registration and process identity checks completed without errors");
            Console.WriteLine($"DESCENDANT_DNS_PASS: x{architecture}");
        }
        finally
        {
            stop.Cancel();
            try { await server; } catch (OperationCanceledException) { }
            foreach (var error in broker.Errors) Console.WriteLine("BROKER_ERROR: " + error);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    sealed class Broker(WindowsProcessCatalog catalog)
    {
        public AppConfiguration Configuration = AppConfiguration.CreateDefault();
        public int BrokenPid;
        public ConcurrentQueue<string> Errors { get; } = new();
        public async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream("ProxyDesk.Dns.Tests.v1", PipeDirection.InOut, 32, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try { await pipe.WaitForConnectionAsync(token); _ = ReplyAsync(pipe, token); }
                catch { pipe.Dispose(); throw; }
            }
        }
        private async Task ReplyAsync(NamedPipeServerStream pipe, CancellationToken token)
        {
            await using (pipe)
            {
                try
                {
                    if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid)) throw new IOException("Cannot identify audit client.");
                    var header = new byte[12];
                    var received = await pipe.ReadAsync(header, token);
                    // A startup notifier can exit after connecting without sending a request.
                    if (received == 0) return;
                    await pipe.ReadExactlyAsync(header.AsMemory(received), token);
                    var operation = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
                    var payload = new byte[BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8))];
                    if (payload.Length > 0) await pipe.ReadExactlyAsync(payload, token);
                    var action = 0;
                    if (operation == 5)
                    {
                        var childPid = BinaryPrimitives.ReadInt32LittleEndian(payload);
                        var path = catalog.RegisterChild((int)pid, childPid, BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(4)));
                        action = WindowsApplicationDns.AttachmentMode(Configuration,
                            RuleEngine.Evaluate(Configuration, catalog.GetLineage(childPid, path), AppPlatform.Windows));
                    }
                    else if (operation is 1 or 3 or 4)
                    {
                        using var process = Process.GetProcessById((int)pid);
                        var decision = RuleEngine.Evaluate(Configuration, catalog.GetLineage((int)pid, process.MainModule!.FileName), AppPlatform.Windows);
                        if (pid == BrokenPid) return;
                        action = decision.Action == RoutingAction.Direct ? 1 : 2;
                    }
                    var reply = new byte[8]; BinaryPrimitives.WriteInt32LittleEndian(reply, action);
                    await pipe.WriteAsync(reply, token);
                }
                catch (Exception error) when (error is not OperationCanceledException) { Errors.Enqueue(error.ToString()); }
            }
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientProcessId(nint pipe, out uint pid);
    }
}
