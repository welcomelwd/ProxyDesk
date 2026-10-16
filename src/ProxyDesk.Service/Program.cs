using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProxyDesk.Service;

var setupIndex = Array.IndexOf(args, "--prepare-network");
if (setupIndex >= 0)
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows network setup is required.");
    Environment.ExitCode = await NetworkSetupRunner.RunAsync(args[setupIndex + 1]); return;
}

var installIndex = Array.IndexOf(args, "--initialize-owner");
if (installIndex >= 0)
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows service installation is required.");
    var options = new ServiceOptions(args);
    Installation.Initialize(options.DataDirectory, args[installIndex + 1]);
    Installation.RegisterService();
    Console.WriteLine("SERVICE_INITIALIZE_PASS");
    return;
}
var builder = Host.CreateApplicationBuilder(args);
if (OperatingSystem.IsWindows() && !args.Contains("--console")) builder.Services.AddWindowsService(options => options.ServiceName = "ProxyDesk");
builder.Services.AddSingleton(new ServiceOptions(args));
builder.Services.AddSingleton<EngineService>();
builder.Services.AddHostedService<ControlServer>();
builder.Services.AddHostedService<DnsServer>();
builder.Services.AddHostedService<ApplicationDnsWorker>();
builder.Services.AddHostedService<AutomationWorker>();
await builder.Build().RunAsync();
