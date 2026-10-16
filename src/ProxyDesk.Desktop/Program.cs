using Avalonia;
using Avalonia.Media;

namespace ProxyDesk.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().With(new FontManagerOptions
    {
        DefaultFamilyName = OperatingSystem.IsWindows() ? "Segoe UI" : "Noto Sans",
        FontFallbacks = [new() { FontFamily = new FontFamily("Microsoft YaHei UI") }, new() { FontFamily = new FontFamily("Noto Sans CJK SC") }]
    }).LogToTrace();
}
