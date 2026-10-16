using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ProxyDesk.Core;
using ProxyDesk.Platform.Windows;

namespace ProxyDesk.Desktop;

internal sealed partial class Pages(MainWindow owner, MainViewModel vm)
{
    private Window? currentForm;
    private Window DialogOwner => currentForm ?? owner;
    private static readonly string[] ProxyLabels = ["SOCKS5", "HTTP", "HTTPS"];
    private static IBrush Color(string value) => ThemePalette.Brush(value);
    private static TextBlock Text(string value, double size = 14, string color = "#29292D", bool bold = false) =>
        new() { Text = value, FontSize = Math.Max(12, size), Foreground = color == "#FFFFFF" ? Brushes.White : Color(color), FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center };
    private TextBlock Live(string property, double size = 14, string color = "#29292D", bool bold = false)
    {
        var text = Text("", size, color, bold); text.Bind(TextBlock.TextProperty, new Binding(property) { Source = vm }); return text;
    }
    private Button Button(string label, Func<Task> action, bool primary = false)
    {
        var button = new Button { Content = label, VerticalAlignment = VerticalAlignment.Center };
        if (primary) button.Classes.Add("primary");
        button.Click += async (_, _) =>
        {
            button.SetCurrentValue(Control.IsEnabledProperty, false);
            try { await action(); }
            catch (Exception error) when (error is not OutOfMemoryException) { vm.Error = error.GetBaseException().Message; }
            finally { button.SetCurrentValue(Control.IsEnabledProperty, true); }
        };
        return button;
    }
    private static StackPanel Horizontal(params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center };
        foreach (var child in children) panel.Children.Add(child); return panel;
    }
    private static StackPanel Vertical(params Control[] children)
    {
        var panel = new StackPanel { Spacing = 14 };
        foreach (var child in children) panel.Children.Add(child); return panel;
    }
    private static Border Card(Control content, string background = "#FFFFFF") =>
        new() { Child = content, Background = Color(background), CornerRadius = new CornerRadius(12), Padding = new Thickness(22),
            BorderBrush = Color("#E3E8EF"), BorderThickness = new Thickness(1) };
    private static Control Padded(Control content) => new Border { Child = content, Padding = new Thickness(24, 12, 24, 24) };
    private static ScrollViewer Scroll(Control content) => new() { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private static Control WithToolbar(Control toolbar, Control content)
    {
        if (toolbar is StackPanel { Orientation: Orientation.Horizontal } horizontal)
        {
            var wrap = new WrapPanel();
            foreach (var child in horizontal.Children.ToArray())
            {
                horizontal.Children.Remove(child); child.Margin = new Thickness(0, 0, horizontal.Spacing, 4); wrap.Children.Add(child);
            }
            toolbar = wrap;
        }
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        grid.Children.Add(new Border { Child = toolbar, Padding = new Thickness(20, 12), BorderBrush = Color("#E3E8EF"), BorderThickness = new Thickness(0, 0, 0, 1) });
        Grid.SetRow(content, 1); grid.Children.Add(content); return grid;
    }
    public Control Build(string page) => page switch
    {
        "仪表盘" => Dashboard(), "代理" => Proxies(), "规则" => Rules(), "配置" => Profiles(),
        "自动化" => Automations(), "连接" => Connections(), "活动" => Activities(), "设置" => SettingsPage(), _ => Dashboard()
    };

    private Control Automations()
    {
        var enabled = MonitorSwitch("自动切换配置", vm.Configuration.Settings.AutomationEnabled);
        enabled.Click += async (_, _) => { var configuration = JsonDefaults.Clone(vm.Configuration); configuration.Settings.AutomationEnabled = enabled.IsChecked == true; try { await vm.SaveAsync(configuration); } catch (Exception error) { vm.Error = error.Message; } };
        var list = new StackPanel { Spacing = 12 };
        foreach (var automation in vm.Configuration.Automations)
        {
            var profile = vm.Configuration.Profiles.Single(p => p.Id == automation.ProfileId);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(Horizontal(ProxyIcon("wifi", size: 22), Text(automation.Ssid, 15, bold: true), ProxyIcon("arrow", "#667085", 16), Text(profile.Name, 14, "#1685FF")));
            var remove = Button("删除", async () => { var configuration = JsonDefaults.Clone(vm.Configuration); configuration.Automations.RemoveAll(a => a.Id == automation.Id); await vm.SaveAsync(configuration); });
            Grid.SetColumn(remove, 1); grid.Children.Add(remove); list.Children.Add(Card(grid));
        }
        if (list.Children.Count == 0) list.Children.Add(Text("还没有 Wi-Fi 自动化。添加 SSID 与配置的对应关系后，连接该网络会自动切换。", 13, "#8A8B93"));
        return Scroll(Padded(Vertical(Card(Horizontal(Text("自动切换配置", 15, bold: true), enabled, Live(nameof(vm.SsidText), 12, "#8A8B93"))),
            Horizontal(Text("Wi-Fi 网络", 15, bold: true), CompactButton("添加网络", "add", EditAutomationAsync)), list)));
    }

    private static Border Divider() => new() { Height = 1, Background = Color("#DDDEE2") };

    private async Task EditAutomationAsync()
    {
        var ssid = new TextBox { Text = vm.State?.Ssid ?? "" }; var profiles = vm.Configuration.Profiles.ToArray();
        var profile = new ComboBox { ItemsSource = profiles.Select(p => p.Name).ToArray(), SelectedIndex = Array.FindIndex(profiles, p => p.Id == vm.Configuration.ActiveProfileId), HorizontalAlignment = HorizontalAlignment.Stretch };
        await FormAsync("添加 Wi-Fi 网络", [Field("SSID（区分大小写）", ssid), Field("连接后启用配置", profile), Text("离开该网络或连接未配置网络时，保持当前配置。", 12, "#8A8B93")], async () =>
        {
            var configuration = JsonDefaults.Clone(vm.Configuration); configuration.Automations.Add(new() { Ssid = ssid.Text ?? "", ProfileId = profiles[profile.SelectedIndex].Id }); await vm.SaveAsync(configuration);
        });
    }
    private static Control Field(string label, Control input) => Vertical(Text(label, 12, "#797A83"), input);
    private async Task FormAsync(string title, IEnumerable<Control> fields, Func<Task> save)
    {
        var dialog = new Window { Title = title, Width = 540, MaxHeight = 780, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var error = new TextBlock { Foreground = Color("#D84E48"), TextWrapping = TextWrapping.Wrap, IsVisible = false, FontSize = 12 };
        var body = new StackPanel { Spacing = 13, Margin = new Thickness(25) }; body.Children.Add(Text(title, 19, bold: true));
        foreach (var field in fields) body.Children.Add(field); body.Children.Add(error);
        var cancel = new Button { Content = "取消" }; cancel.Click += (_, _) => dialog.Close();
        var submit = new Button { Content = "保存", IsDefault = true }; submit.Classes.Add("primary");
        submit.Click += async (_, _) =>
        {
            submit.IsEnabled = false;
            try { await save(); dialog.Close(); }
            catch (Exception failure) { error.Text = failure.GetBaseException().Message; error.IsVisible = true; }
            finally { submit.IsEnabled = true; }
        };
        var actions = Horizontal(cancel, submit); actions.HorizontalAlignment = HorizontalAlignment.Right; actions.Margin = new Thickness(0, 9, 0, 0); body.Children.Add(actions);
        dialog.Content = Scroll(body);
        var parent = DialogOwner; var previousForm = currentForm; currentForm = dialog;
        try { await dialog.ShowDialog(parent); }
        finally { currentForm = previousForm; }
    }
    private async Task<string?> SavePathAsync(string title, string name)
    {
        var file = await DialogOwner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = name, ShowOverwritePrompt = true });
        return file?.Path.LocalPath;
    }
    private async Task<string?> OpenPathAsync(string title)
    {
        var files = await DialogOwner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("JSON 配置") { Patterns = ["*.json"] }] });
        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }
    private async Task ExportConfigurationAsync()
    {
        var path = await SavePathAsync("导出配置（不含密码）", "ProxyDesk-backup.json"); if (path is null) return;
        var configuration = JsonDefaults.Clone(vm.Configuration); foreach (var proxy in configuration.Proxies) proxy.HasPassword = false;
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(configuration, JsonDefaults.Options), new UTF8Encoding(false));
    }
    private async Task ImportConfigurationAsync()
    {
        var path = await OpenPathAsync("导入配置备份"); if (path is null) return;
        var configuration = JsonSerializer.Deserialize<AppConfiguration>(await File.ReadAllTextAsync(path), JsonDefaults.Options) ?? throw new InvalidDataException("Invalid configuration file.");
        await FormAsync("恢复配置", [Text("将使用所选文件替换当前配置。备份不包含密码，新代理需要重新填写密码。", 13)], () => vm.SaveAsync(configuration));
    }
    private sealed record RuleBundle(int Version, List<ProxyServer> Proxies, List<ApplicationRule> Rules);
    private async Task ExportRulesAsync()
    {
        var path = await SavePathAsync("导出当前配置规则", "ProxyDesk-rules.json"); if (path is null) return;
        var bundle = new RuleBundle(1, JsonDefaults.Clone(vm.Configuration.Proxies), JsonDefaults.Clone(vm.Configuration.ActiveProfile.Rules));
        foreach (var proxy in bundle.Proxies) proxy.HasPassword = false;
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(bundle, JsonDefaults.Options));
    }
    private async Task ImportRulesAsync()
    {
        var path = await OpenPathAsync("导入规则"); if (path is null) return;
        var bundle = JsonSerializer.Deserialize<RuleBundle>(await File.ReadAllTextAsync(path), JsonDefaults.Options) ?? throw new InvalidDataException("Invalid rules file.");
        if (bundle.Version != 1) throw new InvalidDataException("Unsupported rules version.");
        var configuration = JsonDefaults.Clone(vm.Configuration);
        foreach (var proxy in bundle.Proxies) if (configuration.Proxies.All(p => p.Id != proxy.Id)) configuration.Proxies.Add(proxy);
        configuration.ActiveProfile.Rules.AddRange(bundle.Rules.Select(r => r with { Id = Guid.NewGuid() })); await vm.SaveAsync(configuration);
    }
    private async Task ExportConnectionsAsync()
    {
        var path = await SavePathAsync("导出连接记录", "ProxyDesk-connections.json"); if (path is not null) await File.WriteAllTextAsync(path, JsonSerializer.Serialize(vm.State?.Connections, JsonDefaults.Options));
    }
    private async Task ExportActivitiesAsync()
    {
        var path = await SavePathAsync("导出活动日志", "ProxyDesk-activity.txt"); if (path is not null) await File.WriteAllLinesAsync(path, vm.Activities.Select(a => $"{a.Time} [{a.Level}] {a.Message}"));
    }
    private async Task CollectDiagnosticsAsync()
    {
        var path = await SavePathAsync("收集诊断日志", "ProxyDesk-diagnostics.json"); if (path is null) return;
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { version = "0.1.1", os = Environment.OSVersion.ToString(),
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, capabilities = vm.State?.Capabilities,
            error = vm.State?.LastError, activities = vm.State?.Activities }, JsonDefaults.Options));
    }
    private async Task InfoAsync(string title, string information)
    {
        var content = new SelectableTextBlock { Text = information, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Color("#29292D") };
        await StyledEditorAsync(title, content, () => Task.CompletedTask, () => true, [], content, new TextBlock(), "完成", 620, showCancel: false);
    }
}
