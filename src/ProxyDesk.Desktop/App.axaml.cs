using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace ProxyDesk.Desktop;

public sealed partial class App : Application
{
    public override void Initialize() { AvaloniaXamlLoader.Load(this); ThemePalette.RefreshResources(); }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            lifetime.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
