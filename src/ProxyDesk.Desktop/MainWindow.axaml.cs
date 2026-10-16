using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

namespace ProxyDesk.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel viewModel = new();
    private readonly Dictionary<string, Button> navigationButtons = [];
    private TrayIcon? tray;
    private bool exiting;
    private readonly Pages pages;
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this); DataContext = viewModel; pages = new Pages(this, viewModel);
        if (PortableSession.Enabled) Title = "ProxyDesk · 便携版";
        var navigation = this.FindControl<StackPanel>("Navigation")!;
        this.FindControl<ContentControl>("SidebarSymbol")!.Content = Pages.ProxyIcon("sidebar", "#667085", 20);
        void Group(string text) => navigation.Children.Add(new TextBlock { Text = text, Foreground = ThemePalette.Brush("#667085"), FontSize = 12, Margin = new Thickness(10, 18, 0, 5) });
        void Item(string text, string icon)
        {
            var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12 };
            var symbol = (Viewbox)Pages.ProxyIcon(icon, size: 19);
            var label = new TextBlock { Text = text, FontSize = 15, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            panel.Children.Add(symbol); panel.Children.Add(label);
            var button = new Button { Content = panel, Foreground = ThemePalette.Brush("#667085") }; button.Classes.Add("nav");
            ((Avalonia.Controls.Shapes.Path)symbol.Child!).Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, new Binding("Foreground") { Source = button });
            label.Bind(TextBlock.ForegroundProperty, new Binding("Foreground") { Source = button });
            button.Click += (_, _) => Navigate(text); navigation.Children.Add(button); navigationButtons.Add(text, button);
        }
        Item("仪表盘", "test"); Group("配置"); Item("代理", "server"); Item("规则", "rules"); Item("配置", "folder");
        Group("自动化"); Item("自动化", "wifi"); Group("监控"); Item("连接", "connections"); Item("活动", "history");
        navigation.Children.Add(new Border { Height = 18 }); Item("设置", "edit");
        this.FindControl<Button>("MinimizeButton")!.Click += (_, _) => WindowState = WindowState.Minimized;
        this.FindControl<Button>("MaximizeButton")!.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        this.FindControl<Button>("CloseWindowButton")!.Click += (_, _) => Close();
        this.FindControl<Border>("TitleBar")!.PointerPressed += (_, args) => { if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(args); };
        this.FindControl<Border>("TitleBar")!.DoubleTapped += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        this.FindControl<Button>("DismissErrorButton")!.Click += (_, _) => viewModel.Error = null;
        viewModel.PropertyChanged += ViewModelChanged;
        viewModel.ConfigurationChanged += () => { ApplyTheme(); Navigate(viewModel.CurrentPage); };
        Navigate("仪表盘");
        Opened += async (_, _) => { SetupTray(); await viewModel.InitializeAsync(); };
        Closing += (_, args) => { if (!exiting && tray is { IsVisible: true }) { args.Cancel = true; Hide(); } };
        Closed += (_, _) => { viewModel.Dispose(); tray?.Dispose(); };
    }
    public void Navigate(string page)
    {
        viewModel.CurrentPage = page;
        this.FindControl<TextBlock>("PageTitle")!.Text = page;
        foreach (var (name, button) in navigationButtons)
        {
            button.Classes.Set("selected", name == page);
            button.Foreground = name == page ? Brushes.White : ThemePalette.Brush("#667085");
        }
        this.FindControl<ContentControl>("PageContent")!.Content = pages.Build(page);
    }
    private void ViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.Error))
        {
            this.FindControl<Border>("ErrorBanner")!.IsVisible = viewModel.Error is not null;
            this.FindControl<TextBlock>("ErrorText")!.Text = viewModel.Error;
        }
        if (args.PropertyName == nameof(MainViewModel.Connected))
        {
            this.FindControl<TextBlock>("ConnectionStatus")!.Text = viewModel.Connected ? PortableSession.Enabled ?
                PortableSession.Owns(viewModel.State?.Runtime) ? "便携后台已连接" : "已连接本机已有后台" : "后台服务已连接" : "后台服务未连接";
            this.FindControl<Border>("ServiceStatusDot")!.Background = ThemePalette.Brush(viewModel.Connected ? "#2EBB55" : "#F05E59");
        }
    }
    private void ApplyTheme()
    {
        var theme = viewModel.Configuration.Settings.Theme;
        if (Application.Current is { } application) application.RequestedThemeVariant = theme switch
        {
            "Light" => Avalonia.Styling.ThemeVariant.Light,
            "Dark" => Avalonia.Styling.ThemeVariant.Dark,
            _ => Avalonia.Styling.ThemeVariant.Default
        };
        ThemePalette.RefreshResources();
    }
    private void SetupTray()
    {
        var menu = new NativeMenu();
        var show = new NativeMenuItem("显示 ProxyDesk"); show.Click += (_, _) => { Show(); WindowState = WindowState.Normal; Activate(); };
        var toggle = new NativeMenuItem("启用 / 停止代理"); toggle.Click += async (_, _) => await viewModel.RunAsync(viewModel.ToggleProxyAsync);
        var exit = new NativeMenuItem("退出并停止代理"); exit.Click += async (_, _) =>
        {
            try
            {
                await viewModel.ExitAsync();
                exiting = true; tray?.Dispose(); (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            { viewModel.Error = failure.GetBaseException().Message; Show(); Activate(); }
        };
        menu.Add(show); menu.Add(toggle); menu.Add(new NativeMenuItemSeparator()); menu.Add(exit);
        tray = new TrayIcon { Icon = Icon, ToolTipText = "ProxyDesk · 应用代理规则", Menu = menu, IsVisible = true };
        tray.Clicked += (_, _) => { Show(); Activate(); };
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { tray });
    }
}
