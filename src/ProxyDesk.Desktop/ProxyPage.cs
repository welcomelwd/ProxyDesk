using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ProxyDesk.Core;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace ProxyDesk.Desktop;

internal sealed partial class Pages
{
    internal static Control ProxyIcon(string icon, string color = "#1685FF", double size = 16)
    {
        var geometry = icon switch
        {
            "test" => "M3 17A10 10 0 1 1 21 17 M12 12L17 7 M5 7L7 9 M12 2V5 M19 7L17 9 M2 12H5 M19 12H22 M10 17H14",
            "link" => "M10 13A5 5 0 0 0 17 13L20 10A5 5 0 0 0 13 3L10 6 M14 11A5 5 0 0 0 7 11L4 14A5 5 0 0 0 11 21L14 18",
            "edit" => "M9 3H15L16 6L19 7L22 12L19 17L16 18L15 21H9L8 18L5 17L2 12L5 7L8 6Z M16 12A4 4 0 1 1 8 12A4 4 0 1 1 16 12",
            "delete" => "M3 6H21 M9 6V3H15V6 M6 6L7 21H17L18 6 M10 10V17 M14 10V17",
            "eye" => "M2 12Q12 -1 22 12Q12 25 2 12Z M15 12A3 3 0 1 1 9 12A3 3 0 1 1 15 12",
            "info" => "M22 12A10 10 0 1 1 2 12A10 10 0 1 1 22 12 M12 11V17 M12 7V8",
            "check" => "M5 12L10 17L19 7",
            "globe" => "M22 12A10 10 0 1 1 2 12A10 10 0 1 1 22 12 M2 12H22 M12 2C5 7 5 17 12 22C19 17 19 7 12 2 M4 6H20 M4 18H20",
            "folder" => "M3 5H10L12 8H21V20H3Z",
            "package" => "M12 2L22 7V17L12 22L2 17V7Z M2 7L12 12L22 7 M12 12V22 M7 4.5L17 9.5",
            "import" => "M12 2V14 M7 9L12 14L17 9 M4 12V21H20V12",
            "export" => "M12 15V2 M7 7L12 2L17 7 M4 12V21H20V12",
            "more" => "M4 11H5V13H4Z M11.5 11H12.5V13H11.5Z M19 11H20V13H19Z",
            "copy" => "M8 8H21V21H8Z M16 8V3H3V16H8",
            "play" => "M7 3L21 12L7 21Z",
            "wifi" => "M2 8Q12 0 22 8 M5 12Q12 6 19 12 M8 16Q12 12 16 16 M11 20H13",
            "activity" => "M2 12H6L9 3L14 21L17 12H22",
            "server" => "M3 3H21V10H3Z M3 14H21V21H3Z M6 6H7 M6 17H7 M11 6H18 M11 17H18",
            "rules" => "M3 4H21V20H3Z M6 8H8 M11 8H18 M6 12H8 M11 12H18 M6 16H8 M11 16H18",
            "connections" => "M3 7H21 M16 2L21 7L16 12 M21 17H3 M8 12L3 17L8 22",
            "history" => "M3 4V10H9 M3 10A9 9 0 1 1 3 16 M12 7V12L16 15",
            "sidebar" => "M3 3H21V21H3Z M9 3V21 M5 7H7 M5 11H7",
            "app" => "M3 4H21V20H3Z M3 9H21 M6 6.5H7 M10 6.5H11",
            "search" => "M16 10A6 6 0 1 1 4 10A6 6 0 1 1 16 10 M14.5 14.5L21 21",
            "arrow" => "M3 12H21 M15 6L21 12L15 18",
            "refresh" => "M21 3V9H15 M21 9A9 9 0 1 0 21 15",
            "warning" => "M12 3L23 21H1Z M12 9V14 M12 17V18",
            "error" => "M22 12A10 10 0 1 1 2 12A10 10 0 1 1 22 12 M8 8L16 16 M16 8L8 16",
            "close" => "M6 6L18 18 M18 6L6 18",
            _ => "M12 4V20 M4 12H20"
        };
        return new Viewbox { Width = size, Height = size, Child = new ShapePath { Width = 24, Height = 24,
            Data = Geometry.Parse(geometry), Stroke = color == "#FFFFFF" ? Brushes.White : Color(color), StrokeThickness = 1.9,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round } };
    }

    private Button ProxyIconButton(string label, string icon, Func<Task> action, string color = "#1685FF")
    {
        var button = Button(label, action);
        button.Content = ProxyIcon(icon, color);
        button.Classes.Add("proxy-icon");
        ToolTip.SetTip(button, label); AutomationProperties.SetName(button, label);
        return button;
    }

    private static Control ProxyStatus(string label, string color)
    {
        var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Color(color), VerticalAlignment = VerticalAlignment.Center };
        var panel = Horizontal(dot, Text(label)); panel.Spacing = 6; return panel;
    }

    private Control Proxies()
    {
        var table = new DataGrid { ItemsSource = vm.Proxies, AutoGenerateColumns = false, IsReadOnly = true,
            RowHeight = 54, ColumnHeaderHeight = 36, RowBackground = Brushes.Transparent,
            GridLinesVisibility = DataGridGridLinesVisibility.None, CanUserReorderColumns = false,
            SelectionMode = DataGridSelectionMode.Single, Background = Brushes.Transparent };
        table.Classes.Add("proxy-table");
        void Column(string title, double width, Func<ProxyRow, Control> content, bool fixedWidth = false) => table.Columns.Add(
            new DataGridTemplateColumn { Header = title, Width = new DataGridLength(width, fixedWidth ? DataGridLengthUnitType.Pixel : DataGridLengthUnitType.Star),
                CellTemplate = new FuncDataTemplate<ProxyRow>((row, _) => row is null ? null : content(row)) });
        Column("名称", 1.35, row =>
        {
            var name = Text(row.Name, bold: true); name.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTip.SetTip(name, row.Name); return name;
        });
        Column("类型", .9, row =>
        {
            var (foreground, background) = row.Kind switch
            {
                "SOCKS5" => ("#AF55D5", "#F3E5F9"), "HTTPS" => ("#299B8D", "#E0F2F0"), _ => ("#1685FF", "#E1EFFF")
            };
            return new Border { Child = Text(row.Kind, 11, foreground), Background = Color(background), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        });
        Column("服务器", 1.65, row =>
        {
            var server = Text(row.Server, color: "#838387"); server.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTip.SetTip(server, row.Server); return server;
        });
        Column("延迟", 1.05, row =>
        {
            var color = row.TestSuccess == false ? "#F05E59" : row.LatencyMs is not { } latency ? "#A4A4A8" :
                latency < 200 ? "#34C759" : latency < 500 ? "#F5BF00" : "#FF9500";
            var status = ProxyStatus(row.Latency, color); ToolTip.SetTip(status, row.Detail); return status;
        });
        Column("健康度", 1.15, row =>
        {
            var status = Horizontal(ProxyStatus(row.Health, row.TestSuccess is null ? "#929296" : row.TestSuccess.Value ? "#34C759" : "#F05E59"),
                ProxyIconButton("测试详情", "info", () => InfoAsync(row.Name, row.Detail), "#929296"));
            status.Spacing = 2; return status;
        });
        Column("认证", .55, row => Text(row.Authentication, 12, "#929296"));
        Column("内网", .55, row => Text(row.PrivateNetwork == "允许" ? "允许" : "—", 12, "#929296"));
        Column("操作", 114, row =>
        {
            ProxyServer Proxy() => vm.Configuration.Proxies.Single(p => p.Id == row.Id);
            var actions = Horizontal(
                ProxyIconButton("测试代理", "test", () => vm.TestAsync(row.Id)),
                ProxyIconButton("编辑代理", "edit", () => EditProxyAsync(Proxy()), "#85858B"),
                ProxyIconButton("删除代理", "delete", async () =>
                {
                    var configuration = JsonDefaults.Clone(vm.Configuration);
                    if (configuration.Profiles.Any(p => p.DefaultProxyId == row.Id || p.Rules.Any(r => r.ProxyId == row.Id)))
                        throw new InvalidOperationException("该代理仍被规则或默认动作引用，请先解除引用。");
                    configuration.Proxies.RemoveAll(p => p.Id == row.Id); await vm.SaveAsync(configuration);
                }, "#F05E59"));
            actions.Spacing = 3; return actions;
        }, true);
        table.DoubleTapped += async (_, _) =>
        {
            if (table.SelectedItem is ProxyRow row) await vm.RunAsync(() => EditProxyAsync(vm.Configuration.Proxies.Single(p => p.Id == row.Id)));
        };
        var stripes = new ProxyTableBackdrop(); stripes.Bind(ProxyTableBackdrop.RowCountProperty, new Binding("Count") { Source = vm.Proxies });
        var body = new Grid { Background = Color("#FFFFFF") }; body.Children.Add(stripes); body.Children.Add(table);
        var testAll = Button("全部测试", () => vm.TestAsync(), true);
        testAll.Content = Horizontal(ProxyIcon("test", "#FFFFFF", 13), Text("全部测试", 12, "#FFFFFF", true)); testAll.Classes.Add("proxy-toolbar");
        var testUrl = ProxyIconButton("测试 URL", "link", () => Task.CompletedTask, "#66666C"); testUrl.Classes.Add("proxy-toolbar");
        testUrl.Click += (_, _) => ShowTestUrl(testUrl);
        var add = ProxyIconButton("添加代理", "add", () => EditProxyAsync(), "#FFFFFF"); add.Classes.Add("primary"); add.Classes.Add("proxy-toolbar");
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var left = Horizontal(testAll, testUrl); left.Spacing = 10; toolbar.Children.Add(left);
        Grid.SetColumn(add, 1); toolbar.Children.Add(add);
        return WithToolbar(toolbar, body);
    }

    private static TextBox ProxyInput(string? value, string name, string? placeholder = null)
    {
        var input = new TextBox { Text = value, Name = name, PlaceholderText = placeholder };
        input.Classes.Add("proxy-input"); return input;
    }

    private static Control ProxyFormRow(string label, Control input, double labelWidth = 170)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions { new(labelWidth, GridUnitType.Pixel), new(1, GridUnitType.Star) }, MinHeight = 46 };
        row.Children.Add(Text(label)); input.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(input, 1); row.Children.Add(input); return row;
    }

    private static Border ProxyFormGroup(params Control[] rows)
    {
        var body = new StackPanel();
        for (var index = 0; index < rows.Length; index++)
        {
            if (index > 0) body.Children.Add(new Border { Height = 1, Background = Color("#DDDEE2") });
            body.Children.Add(rows[index]);
        }
        return new Border { Child = body, Padding = new Thickness(12, 0), Background = Color("#E9E9EB"),
            BorderBrush = Color("#DDDEE2"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7) };
    }

    private async Task EditProxyAsync(ProxyServer? existing = null)
    {
        var proxy = existing is null ? new ProxyServer() : JsonDefaults.Clone(existing);
        var title = existing is null ? "添加代理" : "编辑代理";
        var dialog = new Window { Title = title, Width = 500, MaxHeight = owner.Bounds.Height - 48, SizeToContent = SizeToContent.Height,
            CanResize = false, ShowInTaskbar = false, WindowDecorations = WindowDecorations.None, Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent], WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var name = ProxyInput(proxy.Name, "ProxyNameInput");
        var host = ProxyInput(proxy.Host, "ProxyHostInput");
        var port = ProxyInput(proxy.Port.ToString(), "ProxyPortInput");
        var username = ProxyInput(proxy.Username, "ProxyUsernameInput");
        var password = ProxyInput(null, "ProxyPasswordInput", proxy.HasPassword ? "留空保留已有密码" : null); password.PasswordChar = '●';
        var protocols = new RadioButton[ProxyLabels.Length];
        var protocolGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), Background = Color("#DDDEE2") };
        var groupName = Guid.NewGuid().ToString();
        for (var index = 0; index < protocols.Length; index++)
        {
            var option = new RadioButton { Content = ProxyLabels[index], GroupName = groupName, IsChecked = index == (int)proxy.Kind };
            option.Classes.Add("proxy-segment"); protocols[index] = option; Grid.SetColumn(option, index); protocolGrid.Children.Add(option);
        }
        var reveal = ProxyIconButton("显示或隐藏密码", "eye", () => { password.PasswordChar = password.PasswordChar == '●' ? '\0' : '●'; return Task.CompletedTask; }, "#85858B");
        var passwordRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,28") }; passwordRow.Children.Add(password); Grid.SetColumn(reveal, 1); passwordRow.Children.Add(reveal);
        var clearPassword = new CheckBox { Content = "清除已有密码", IsVisible = proxy.HasPassword, FontSize = 12 };
        var privateNetwork = new ToggleButton { IsChecked = proxy.AllowPrivateNetworks, HorizontalAlignment = HorizontalAlignment.Right };
        privateNetwork.Classes.Add("proxy-switch"); AutomationProperties.SetName(privateNetwork, "可访问私有网络");
        ToolTip.SetTip(privateNetwork, "开启后，私有地址也会交给所选代理；关闭时私有网络直连。");
        var failure = Text("停止连接", 12, "#85858B"); failure.HorizontalAlignment = HorizontalAlignment.Right;
        ToolTip.SetTip(failure, "代理连接失败时停止连接，保持当前规则要求的出口。");
        var fields = new StackPanel { Spacing = 12, Margin = new Thickness(24, 0, 24, 18) };
        fields.Children.Add(ProxyFormGroup(ProxyFormRow("名称", name), ProxyFormRow("类型", new Border { Child = protocolGrid, CornerRadius = new CornerRadius(5), ClipToBounds = true }, 58)));
        fields.Children.Add(ProxyFormGroup(ProxyFormRow("服务器地址", host), ProxyFormRow("端口", port)));
        fields.Children.Add(ProxyFormGroup(ProxyFormRow("用户名（可选）", username), ProxyFormRow("密码（可选）", passwordRow), clearPassword));
        fields.Children.Add(ProxyFormGroup(ProxyFormRow("代理失败处理", failure), ProxyFormRow("可访问私有网络", privateNetwork)));
        var error = new TextBlock { Foreground = Color("#D84E48"), TextWrapping = TextWrapping.Wrap, FontSize = 12, IsVisible = false }; fields.Children.Add(error);
        var cancel = new Button { Content = "取消", IsCancel = true }; cancel.Classes.Add("proxy-toolbar"); cancel.Click += (_, _) => dialog.Close();
        var submit = new Button { Content = "保存", IsDefault = true }; submit.Classes.Add("primary"); submit.Classes.Add("proxy-toolbar");
        var saving = false;
        void UpdateSaveState() => submit.IsEnabled = !saving && !string.IsNullOrWhiteSpace(name.Text) && !string.IsNullOrWhiteSpace(host.Text) && int.TryParse(port.Text, out var number) && number is >= 1 and <= 65535;
        name.TextChanged += (_, _) => UpdateSaveState(); host.TextChanged += (_, _) => UpdateSaveState(); port.TextChanged += (_, _) => UpdateSaveState(); UpdateSaveState();
        submit.Click += async (_, _) =>
        {
            saving = true; UpdateSaveState(); error.IsVisible = false;
            try
            {
                proxy.Name = name.Text!.Trim(); proxy.Host = host.Text!.Trim(); proxy.Port = int.Parse(port.Text!);
                proxy.Kind = (ProxyKind)Array.FindIndex(protocols, option => option.IsChecked == true); proxy.Username = username.Text ?? "";
                proxy.AllowPrivateNetworks = privateNetwork.IsChecked == true;
                var configuration = JsonDefaults.Clone(vm.Configuration);
                var index = configuration.Proxies.FindIndex(p => p.Id == proxy.Id);
                if (index < 0) configuration.Proxies.Add(proxy); else configuration.Proxies[index] = proxy;
                var passwords = new Dictionary<Guid, string>();
                if (!string.IsNullOrEmpty(password.Text) || clearPassword.IsChecked == true) passwords[proxy.Id] = clearPassword.IsChecked == true ? "" : password.Text!;
                await vm.SaveAsync(configuration, passwords); dialog.Close();
            }
            catch (Exception failure) { error.Text = failure.GetBaseException().Message; error.IsVisible = true; }
            finally { saving = false; UpdateSaveState(); }
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 58, Margin = new Thickness(20, 0) };
        header.Children.Add(Text(title, 15, bold: true));
        var close = ProxyIconButton("关闭", "close", () => { dialog.Close(); return Task.CompletedTask; }, "#85858B"); Grid.SetColumn(close, 1); header.Children.Add(close);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(20, 16) }; footer.Children.Add(cancel); cancel.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(submit, 1); footer.Children.Add(submit);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") }; layout.Children.Add(header);
        var scroll = Scroll(fields); Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        var bottom = new Border { Child = footer, BorderBrush = Color("#D1D1D4"), BorderThickness = new Thickness(0, 1, 0, 0) }; Grid.SetRow(bottom, 2); layout.Children.Add(bottom);
        dialog.Content = new Border { Child = layout, Background = Color("#EEEEEF"), BorderBrush = Color("#C8C8CC"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), ClipToBounds = true };
        dialog.Opened += (_, _) => { name.Focus(); name.SelectAll(); };
        var scrim = owner.FindControl<Border>("DialogScrim")!; scrim.IsVisible = true;
        var parent = DialogOwner; var previousForm = currentForm; currentForm = dialog;
        try { await dialog.ShowDialog(parent); }
        finally { currentForm = previousForm; scrim.IsVisible = false; }
    }

    private void ShowTestUrl(Control anchor)
    {
        var flyout = new Flyout { Placement = PlacementMode.Bottom }; flyout.FlyoutPresenterClasses.Add("proxy-url-flyout");
        var url = ProxyInput(vm.Configuration.Settings.TestUrl, "ProxyTestUrlInput");
        var error = new TextBlock { FontSize = 12, Foreground = Color("#D84E48"), TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var presets = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, value) in new[] { ("www.google.com", "https://www.google.com/generate_204"),
            ("www.gstatic.com", "https://www.gstatic.com/generate_204"), ("cp.cloudflare.com", "https://cp.cloudflare.com/generate_204") })
        {
            var preset = new Button { Content = label, Margin = new Thickness(0, 0, 6, 4) }; preset.Classes.Add("proxy-preset");
            preset.Click += (_, _) => url.Text = value; presets.Children.Add(preset);
        }
        var reset = new Button { Content = "重置" }; reset.Classes.Add("proxy-toolbar"); reset.Click += (_, _) => url.Text = new Settings().TestUrl;
        var cancel = new Button { Content = "取消" }; cancel.Classes.Add("proxy-toolbar"); cancel.Click += (_, _) => flyout.Hide();
        var save = new Button { Content = "保存", IsDefault = true }; save.Classes.Add("primary"); save.Classes.Add("proxy-toolbar");
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false; error.IsVisible = false;
            try { var configuration = JsonDefaults.Clone(vm.Configuration); configuration.Settings.TestUrl = url.Text?.Trim() ?? ""; await vm.SaveAsync(configuration); flyout.Hide(); }
            catch (Exception failure) { error.Text = failure.GetBaseException().Message; error.IsVisible = true; }
            finally { save.IsEnabled = true; }
        };
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") }; actions.Children.Add(reset); reset.HorizontalAlignment = HorizontalAlignment.Left;
        var right = Horizontal(cancel, save); right.Spacing = 8; Grid.SetColumn(right, 1); actions.Children.Add(right);
        var body = Vertical(Text("测试 URL", 14, bold: true), Text("HTTP 请求测试 — 测量通过代理访问的延迟", 12, "#85858B"), url, presets, error, actions);
        body.Spacing = 12; body.Width = 388; flyout.Content = body;
        flyout.Opened += (_, _) => { url.Focus(); url.SelectAll(); }; flyout.ShowAt(anchor);
    }
}

internal sealed class ProxyTableBackdrop : Control
{
    public static readonly StyledProperty<int> RowCountProperty = AvaloniaProperty.Register<ProxyTableBackdrop, int>(nameof(RowCount));
    public static readonly StyledProperty<double> RowHeightProperty = AvaloniaProperty.Register<ProxyTableBackdrop, double>(nameof(RowHeight), 54);
    public int RowCount { get => GetValue(RowCountProperty); set => SetValue(RowCountProperty, value); }
    public double RowHeight { get => GetValue(RowHeightProperty); set => SetValue(RowHeightProperty, value); }
    static ProxyTableBackdrop() => AffectsRender<ProxyTableBackdrop>(RowCountProperty, RowHeightProperty);
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        for (var y = 36 + Math.Max(1, RowCount) * RowHeight; y < Bounds.Height; y += 60)
            context.DrawRectangle(ThemePalette.Brush("#F4F5F5"), null, new Rect(12, y, Math.Max(0, Bounds.Width - 24), 28), 6, 6);
    }
}
