using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ProxyDesk.Core;
using ProxyDesk.Platform.Windows;

namespace ProxyDesk.Desktop;

internal sealed partial class Pages
{
    private Control SettingsPage()
    {
        var theme = new ComboBox { ItemsSource = new[] { "跟随系统", "浅色", "深色" }, MinWidth = 140,
            SelectedIndex = vm.Configuration.Settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 } };
        AutomationProperties.SetName(theme, "外观");
        theme.SelectionChanged += async (_, _) =>
        {
            if (theme.SelectedIndex < 0) return;
            await vm.RunAsync(async () => { var configuration = JsonDefaults.Clone(vm.Configuration);
                configuration.Settings.Theme = new[] { "System", "Light", "Dark" }[theme.SelectedIndex]; await vm.SaveAsync(configuration); });
        };
        var login = MonitorSwitch("登录时启动界面", vm.Configuration.Settings.LaunchAtLogin); login.IsEnabled = OperatingSystem.IsWindows();
        login.Click += async (_, _) => await vm.RunAsync(async () =>
        {
            var configuration = JsonDefaults.Clone(vm.Configuration); configuration.Settings.LaunchAtLogin = login.IsChecked == true;
            if (OperatingSystem.IsWindows()) new WindowsIntegration().SetLaunchAtLogin(configuration.Settings.LaunchAtLogin);
            await vm.SaveAsync(configuration);
        });
        var autoStart = MonitorSwitch("后台启动时开启代理", vm.Configuration.Settings.StartProxyOnLaunch);
        autoStart.Click += async (_, _) => await vm.RunAsync(async () => { var configuration = JsonDefaults.Clone(vm.Configuration);
            configuration.Settings.StartProxyOnLaunch = autoStart.IsChecked == true; await vm.SaveAsync(configuration); });
        var general = Vertical(
            SettingsGroup("外观", SettingsRow("外观", theme), SettingsRow("语言", Badge("简体中文", "#85858B", "#E4E8EE"))),
            SettingsGroup("启动", SettingsRow("登录时启动界面", login, OperatingSystem.IsWindows() ? "登录 Windows 后打开 ProxyDesk。" : "Linux 登录启动尚未接入。"),
                SettingsRow("后台启动时开启代理", autoStart, "后台服务启动后，自动启用当前配置。")),
            NetworkSettings(),
            SettingsGroup("DNS", SettingsRow("DoH 服务器", CompactButton("DNS 设置…", "edit", EditDnsSettingsAsync),
                "规则开启“DNS 解析走代理”时使用此服务器，默认开启。\n" + vm.Configuration.Settings.DohUrl)),
            SettingsGroup("备份与诊断", SettingsRow("配置备份", Horizontal(CompactButton("导入…", "import", ImportConfigurationAsync), CompactButton("导出…", "export", ExportConfigurationAsync)), "导出文件默认不包含代理密码。"),
                SettingsRow("诊断日志", CompactButton("收集日志…", "export", CollectDiagnosticsAsync))));
        general.Spacing = 20;
        var about = AboutSettings(); about.IsVisible = false;
        var tabs = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Width = 144, HorizontalAlignment = HorizontalAlignment.Center, Background = Color("#DDDEE2") };
        var group = Guid.NewGuid().ToString();
        var common = new RadioButton { Content = "通用", IsChecked = true, GroupName = group }; common.Classes.Add("proxy-segment");
        var information = new RadioButton { Content = "关于", GroupName = group }; information.Classes.Add("proxy-segment"); Grid.SetColumn(information, 1);
        tabs.Children.Add(common); tabs.Children.Add(information);
        common.IsCheckedChanged += (_, _) => { if (common.IsChecked == true) { general.IsVisible = true; about.IsVisible = false; } };
        information.IsCheckedChanged += (_, _) => { if (information.IsChecked == true) { general.IsVisible = false; about.IsVisible = true; } };
        var pages = new Grid(); pages.Children.Add(general); pages.Children.Add(about);
        var body = Vertical(new Border { Child = tabs, CornerRadius = new CornerRadius(6), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center }, pages); body.Spacing = 24;
        return Scroll(new Border { Child = body, Padding = new Thickness(28, 14, 28, 28), MaxWidth = 1150, HorizontalAlignment = HorizontalAlignment.Stretch });
    }

    private Control NetworkSettings()
    {
        var description = Live(nameof(vm.NetworkDetails), 12, "#85858B"); description.TextWrapping = TextWrapping.Wrap;
        var recover = CompactButton("授权启动后台", "play", vm.PrepareNetworkAsync);
        recover.Bind(Control.IsVisibleProperty, new Binding(nameof(vm.NeedsNetworkAuthorization)) { Source = vm });
        recover.Bind(Control.IsEnabledProperty, new Binding(nameof(vm.CanPrepareNetwork)) { Source = vm });
        var result = Live(nameof(vm.NetworkSetupMessage), 12, "#85858B"); result.TextWrapping = TextWrapping.Wrap;
        result.Bind(Control.IsVisibleProperty, new Binding(nameof(vm.NetworkSetupMessage)) { Source = vm, Converter = new FuncValueConverter<string, bool>(value => !string.IsNullOrEmpty(value)) });
        var body = Vertical(SettingsRow("网络组件", Live(nameof(vm.NetworkStatus), 12, "#1685FF")), Divider(),
            SettingsRow("后台服务", Live(nameof(vm.BackendPermission), 12, "#85858B")), description, recover, result);
        body.Spacing = 10; recover.HorizontalAlignment = HorizontalAlignment.Right;
        return Vertical(Text("网络", 12, "#85858B"), Card(body));
    }

    private static Control SettingsGroup(string title, params Control[] rows) => Vertical(Text(title, 12, "#85858B"), SurfaceGroup(rows));

    private static Control SettingsRow(string title, Control value, string? description = null)
    {
        var label = description is null ? (Control)Text(title, 14) : Vertical(Text(title, 14), new TextBlock { Text = description, FontSize = 11,
            Foreground = Color("#85858B"), TextWrapping = TextWrapping.Wrap });
        if (label is StackPanel stack) stack.Spacing = 4;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 24, MinHeight = description is null ? 52 : 64 };
        label.VerticalAlignment = VerticalAlignment.Center; value.VerticalAlignment = VerticalAlignment.Center; value.HorizontalAlignment = HorizontalAlignment.Right;
        row.Children.Add(label); Grid.SetColumn(value, 1); row.Children.Add(value); return row;
    }

    private Control AboutSettings()
    {
        var name = Vertical(Text("ProxyDesk", 25, bold: true), Text("0.1.0", 13, "#85858B")); name.Spacing = 4;
        var logo = new Image { Source = new Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://ProxyDesk.Desktop/Assets/ProxyDesk.png"))), Width = 70, Height = 70 };
        var identity = Horizontal(logo, name); identity.Spacing = 20;
        return Vertical(Card(Vertical(identity, Text("按应用管理代理、直连与阻止规则。", 14, "#85858B"))),
            SettingsGroup("版本信息", SettingsRow("桌面框架", Text("Avalonia / .NET 10", 12, "#85858B")),
                SettingsRow("系统", Text(System.Runtime.InteropServices.RuntimeInformation.OSDescription, 12, "#85858B")),
                SettingsRow("发行方式", Text(PortableSession.Enabled ? "Windows 便携版" : OperatingSystem.IsWindows() ? "Windows 安装版" : "Linux 预览版", 12, "#85858B")),
                SettingsRow("配置目录", Live(nameof(vm.DataDirectoryText), 12, "#85858B")),
                SettingsRow("更新方式", Text(PortableSession.Enabled ? "退出后解压新版，保留 Data 文件夹" : "运行新版安装包", 12, "#85858B"))),
            Card(Vertical(Text("Windows 完整包已包含运行时、WinDivert 和 DNS 组件。", 12, "#85858B"),
                Text("界面和共享核心兼容 Linux；Linux 网络接管将在第二阶段提供。", 12, "#85858B"))));
    }

    private async Task EditDnsSettingsAsync()
    {
        var url = ProxyInput(vm.Configuration.Settings.DohUrl, "DohUrlInput", "https://cloudflare-dns.com/dns-query");
        var bootstrap = ProxyInput(vm.Configuration.Settings.DohBootstrapIp, "DohBootstrapInput", "1.1.1.1");
        var error = new TextBlock { IsVisible = false, Foreground = Color("#F05E59"), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var explanation = Text("查询通过应用所选代理发送；证书按 DoH 地址校验，解析失败时返回错误。", 12, "#85858B"); explanation.TextWrapping = TextWrapping.Wrap;
        var fields = Vertical(SurfaceGroup(ProxyFormRow("DoH HTTPS 地址", url, 140), ProxyFormRow("引导 IP", bootstrap, 140)), explanation, error);
        await StyledEditorAsync("DNS 设置", fields, async () =>
        {
            var configuration = JsonDefaults.Clone(vm.Configuration); configuration.Settings.DohUrl = url.Text!.Trim();
            configuration.Settings.DohBootstrapIp = bootstrap.Text!.Trim(); await vm.SaveAsync(configuration);
        }, () => !string.IsNullOrWhiteSpace(url.Text) && !string.IsNullOrWhiteSpace(bootstrap.Text), [url, bootstrap], url, error, width: 600);
    }
}
