using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using ProxyDesk.Core;

namespace ProxyDesk.Desktop;

internal sealed partial class Pages
{
    private static DataGrid MonitorTable<T>(IEnumerable<T> rows, Func<T, Control> content, double height = 40) where T : class
    {
        var table = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, IsReadOnly = true, RowHeight = height,
            HeadersVisibility = DataGridHeadersVisibility.None, RowBackground = Brushes.Transparent, Margin = new Thickness(18, 8, 18, 0),
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, SelectionMode = DataGridSelectionMode.Single, Background = Color("#FFFFFF") };
        table.Columns.Add(new DataGridTemplateColumn { Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            CellTemplate = new FuncDataTemplate<T>((row, _) => row is null ? null : new Border { Child = content(row), Padding = new Thickness(0, 0, 24, 0) }) });
        table.Classes.Add("monitor-list"); return table;
    }

    private static ToggleButton MonitorSwitch(string name, bool value)
    {
        var toggle = new ToggleButton { IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
        toggle.Classes.Add("proxy-switch"); AutomationProperties.SetName(toggle, name); return toggle;
    }

    private Control MonitorBody(DataGrid table, object rows, string emptyMessage)
    {
        var area = new Grid { Background = Color("#FFFFFF") }; area.Children.Add(table);
        var empty = Text(emptyMessage, 14, "#667085"); empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Top;
        empty.Margin = new Thickness(20, 80, 20, 0); empty.IsHitTestVisible = false;
        empty.Bind(Control.IsVisibleProperty, new Binding("Count") { Source = rows, Converter = new FuncValueConverter<int, bool>(count => count == 0) }); area.Children.Add(empty);
        var count = Text("", 12, "#667085"); count.Bind(TextBlock.TextProperty, new Binding("Count") { Source = rows,
            Converter = new FuncValueConverter<int, string>(value => $"{value} 条记录") });
        var body = new Grid { RowDefinitions = new RowDefinitions("*,Auto") }; body.Children.Add(area);
        var footer = new Border { Child = count, Background = Color("#FFFFFF"), Padding = new Thickness(20, 9),
            BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Color("#E3E8EF") };
        Grid.SetRow(footer, 1); body.Children.Add(footer); return body;
    }

    private static TextBox MonitorSearch(string value, string name, string placeholder)
    {
        var search = ProxyInput(value, name, placeholder); search.Classes.Add("monitor-search");
        search.InnerLeftContent = new Border { Child = ProxyIcon("search", "#667085", 15), Margin = new Thickness(1, 0, 7, 0) };
        return search;
    }

    private Control Connections()
    {
        var search = MonitorSearch(vm.ConnectionSearch, "ConnectionSearchInput", "搜索程序、PID 或目标地址…"); search.HorizontalAlignment = HorizontalAlignment.Stretch;
        search.TextChanged += (_, _) => vm.ConnectionSearch = search.Text ?? "";
        var mode = new ComboBox { ItemsSource = new[] { "包含", "不包含" }, SelectedIndex = vm.ConnectionSearchExclude ? 1 : 0, Width = 94, MinHeight = 30 };
        AutomationProperties.SetName(mode, "连接搜索方式");
        mode.SelectionChanged += (_, _) => { if (mode.SelectedIndex >= 0) vm.ConnectionSearchExclude = mode.SelectedIndex == 1; };
        var record = MonitorSwitch("记录连接", vm.State?.Recording ?? true);
        record.Bind(ToggleButton.IsCheckedProperty, new Binding("State.Recording") { Source = vm, Mode = BindingMode.OneWay });
        record.Click += async (_, _) => await vm.RunAsync(() => vm.OperationAsync("Recording", new ToggleRequest(record.IsChecked == true)));
        var recordLabel = Text("", 13); recordLabel.Bind(TextBlock.TextProperty, new Binding("State.Recording") { Source = vm,
            Converter = new FuncValueConverter<bool, string>(value => value ? "记录中" : "已暂停") });
        var discovery = MonitorSwitch("探索未配置程序", vm.State?.Discovery ?? false);
        discovery.Bind(ToggleButton.IsCheckedProperty, new Binding("State.Discovery") { Source = vm, Mode = BindingMode.OneWay });
        discovery.Click += async (_, _) => await vm.RunAsync(() => vm.OperationAsync("Discovery", new ToggleRequest(discovery.IsChecked == true)));
        ToolTip.SetTip(discovery, "同时记录未匹配规则的直连程序。");
        var clear = CompactButton("清除", "delete", () => vm.OperationAsync("ClearConnections")); clear.Classes.Add("monitor-action");
        var actions = Horizontal(recordLabel, record, Text("探索", 13), discovery, clear,
            ProxyIconButton("导出连接记录", "export", ExportConnectionsAsync, "#667085")); actions.Spacing = 12;
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12, MinHeight = 40 };
        toolbar.Children.Add(mode); Grid.SetColumn(search, 1); toolbar.Children.Add(search); Grid.SetColumn(actions, 2); toolbar.Children.Add(actions);
        var table = MonitorTable(vm.Connections, ConnectionLine);
        table.DoubleTapped += async (_, _) => { if (table.SelectedItem is ConnectionRow row) await vm.RunAsync(() => ConnectionDetailsAsync(row)); };
        var details = MenuAction("连接详情", async () => { if (table.SelectedItem is ConnectionRow row) await ConnectionDetailsAsync(row); });
        var addRule = MenuAction("为此程序创建规则", async () =>
        {
            if (table.SelectedItem is ConnectionRow row) await EditRuleAsync(seed: new() { Name = row.Application,
                Selector = new() { Value = row.Source.ProcessPath, Platform = OperatingSystem.IsWindows() ? AppPlatform.Windows : AppPlatform.Linux }, Action = RoutingAction.Direct });
        });
        table.ContextMenu = new ContextMenu { ItemsSource = new[] { details, addRule } };
        return WithToolbar(toolbar, MonitorBody(table, vm.Connections, "暂无匹配的连接记录"));
    }

    private Control ConnectionLine(ConnectionRow row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("72,46,26,Auto,Auto,28,*,Auto,28"), ColumnSpacing = 6 };
        var time = Text(row.Time, 12, "#667085"); time.FontFamily = new FontFamily("Cascadia Mono, Consolas, Noto Sans Mono"); grid.Children.Add(time);
        var protocol = Badge(row.Protocol, row.Protocol == "UDP" ? "#A55E12" : "#1769D2", row.Protocol == "UDP" ? "#FFF1DF" : "#E1EFFF");
        Grid.SetColumn(protocol, 1); grid.Children.Add(protocol);
        var icon = ProxyIcon("app", "#667085", 18); Grid.SetColumn(icon, 2); grid.Children.Add(icon);
        var application = Text(row.Application.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? row.Application[..^4] : row.Application, 15, bold: true);
        application.MaxWidth = 160; application.TextTrimming = TextTrimming.CharacterEllipsis; ToolTip.SetTip(application, row.Source.ProcessPath);
        Grid.SetColumn(application, 3); grid.Children.Add(application);
        var pid = Text("· " + row.Pid, 12, "#667085"); Grid.SetColumn(pid, 4); grid.Children.Add(pid);
        var arrow = ProxyIcon("arrow", "#98A2B3", 15); Grid.SetColumn(arrow, 5); grid.Children.Add(arrow);
        var destination = Text(row.Destination, 15, "#A55E12"); destination.TextTrimming = TextTrimming.CharacterEllipsis; ToolTip.SetTip(destination, row.Destination);
        Grid.SetColumn(destination, 6); grid.Children.Add(destination);
        var route = Horizontal(); route.Spacing = 9;
        if (row.Source.RuleId is not null) route.Children.Add(Badge("规则", "#237A45", "#E5F6E7"));
        if (row.Source.Action == RoutingAction.Proxy)
        {
            var proxy = Text(row.Proxy, 12, "#7952B3"); proxy.MaxWidth = 150; proxy.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTip.SetTip(proxy, row.Proxy); route.Children.Add(proxy);
        }
        route.Children.Add(Text(row.Action, 12, row.Source.Action == RoutingAction.Block ? "#C83E48" : row.Source.Action == RoutingAction.Direct ? "#237A45" : "#1769D2"));
        Grid.SetColumn(route, 7); grid.Children.Add(route);
        var info = ProxyIconButton("连接详情", "info", () => Task.CompletedTask, "#667085");
        var flyout = new Flyout { Placement = PlacementMode.LeftEdgeAlignedTop }; flyout.FlyoutPresenterClasses.Add("monitor-detail");
        info.Click += (_, _) => { flyout.Content = ConnectionPopover(row, flyout); flyout.ShowAt(info); };
        Grid.SetColumn(info, 8); grid.Children.Add(info); return grid;
    }

    private Control ConnectionPopover(ConnectionRow row, Flyout flyout)
    {
        var rule = vm.Configuration.Profiles.SelectMany(profile => profile.Rules).FirstOrDefault(item => item.Id == row.Source.RuleId);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") }; header.Children.Add(Text("连接详情", 15, bold: true));
        var close = ProxyIconButton("关闭详情", "close", () => { flyout.Hide(); return Task.CompletedTask; }, "#667085"); Grid.SetColumn(close, 1); header.Children.Add(close);
        var path = new SelectableTextBlock { Text = row.Source.ProcessPath, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Color("#667085") };
        var body = Vertical(header, Divider(), Text("可执行文件", 13, bold: true), path, Divider(),
            DetailRow("进程", row.Application + " · PID " + row.Pid), DetailRow("目标", row.Destination),
            DetailRow("连接时间", row.Source.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
            DetailRow("路由动作", row.Action), DetailRow("代理", row.Proxy),
            DetailRow("当前规则", rule?.Name ?? (row.Source.RuleId is null ? "配置默认动作" : "规则已从配置移除")));
        body.Spacing = 10; body.Width = 410;
        if (rule is not null)
        {
            body.Children.Add(DetailRow("匹配方式", rule.Selector.Kind switch { SelectorKind.ExecutableName => "程序名", SelectorKind.DirectoryGlob => "目录通配符", SelectorKind.ScriptPath => "脚本路径", _ => "完整路径" }));
            body.Children.Add(DetailRow("规则路径", rule.Selector.Value));
        }
        if (row.Detail.Length > 0)
        {
            var detail = Text(row.Detail, 12, "#667085"); detail.TextWrapping = TextWrapping.Wrap; body.Children.Add(detail);
        }
        return body;
    }

    private static Control DetailRow(string label, string value)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("94,*"), ColumnSpacing = 10 };
        row.Children.Add(Text(label, 12, "#667085")); var text = new SelectableTextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Color("#29292D") };
        Grid.SetColumn(text, 1); row.Children.Add(text); return row;
    }

    private Task ConnectionDetailsAsync(ConnectionRow row) => InfoAsync("连接详情",
        $"时间：{row.Source.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n协议：{row.Protocol}\n程序：{row.Source.ProcessPath}\nPID：{row.Pid}\n目标：{row.Destination}\n动作：{row.Action}\n代理：{row.Proxy}\n\n{row.Detail}");

    private Control Activities()
    {
        var search = MonitorSearch(vm.ActivitySearch, "ActivitySearchInput", "搜索日志…"); search.Width = 360; search.HorizontalAlignment = HorizontalAlignment.Left;
        search.TextChanged += (_, _) => vm.ActivitySearch = search.Text ?? "";
        var levels = new[] { "", "Info", "Warning", "Error" };
        var level = new ComboBox { ItemsSource = new[] { "全部", "信息", "警告", "错误" }, SelectedIndex = Array.IndexOf(levels, vm.ActivityLevel), Width = 92, MinHeight = 30 };
        AutomationProperties.SetName(level, "日志级别"); level.SelectionChanged += (_, _) => { if (level.SelectedIndex >= 0) vm.ActivityLevel = levels[level.SelectedIndex]; };
        var actions = Horizontal(Text("级别", 13), level, ProxyIconButton("导出当前日志", "export", ExportActivitiesAsync, "#667085"),
            ProxyIconButton("刷新日志", "refresh", () => vm.OperationAsync("GetState"), "#667085"),
            ProxyIconButton("清空活动日志", "delete", () => vm.OperationAsync("ClearActivities"), "#667085")); actions.Spacing = 12;
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 20, MinHeight = 40 }; toolbar.Children.Add(search); Grid.SetColumn(actions, 1); toolbar.Children.Add(actions);
        var table = MonitorTable(vm.Activities, ActivityLine, 38);
        table.DoubleTapped += async (_, _) => { if (table.SelectedItem is ActivityRow row) await vm.RunAsync(() => InfoAsync("活动详情", $"{row.Time}  [{row.Level}]\n\n{row.Message}")); };
        return WithToolbar(toolbar, MonitorBody(table, vm.Activities, "暂无匹配的活动日志"));
    }

    private Control ActivityLine(ActivityRow row)
    {
        var warning = row.Level.Equals("Warning", StringComparison.OrdinalIgnoreCase) || row.Level.Equals("Warn", StringComparison.OrdinalIgnoreCase);
        var error = row.Level.Equals("Error", StringComparison.OrdinalIgnoreCase);
        var icon = ProxyIcon(error ? "error" : warning ? "warning" : "info", error ? "#C83E48" : warning ? "#A55E12" : "#1769D2", 16); ToolTip.SetTip(icon, row.Level);
        var category = row.Message.StartsWith("Configuration ", StringComparison.Ordinal) ? ("配置", "#237A45", "#E5F6E7") :
            row.Message.StartsWith("Proxy ", StringComparison.Ordinal) ? ("代理", "#1769D2", "#E1EFFF") :
            row.Message.StartsWith("Traffic ", StringComparison.Ordinal) || row.Message.StartsWith("ProxyBridge", StringComparison.Ordinal) || row.Message.StartsWith("Shared policy", StringComparison.Ordinal)
                ? ("网络", "#7952B3", "#F3E5F9") : ("服务", "#667085", "#F4F5F5");
        var divider = row.Message.IndexOf(": ", StringComparison.Ordinal);
        var title = divider < 0 ? row.Message : row.Message[..divider]; var description = divider < 0 ? "" : row.Message[(divider + 2)..];
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("24,132,68,Auto,*,28"), ColumnSpacing = 10 }; grid.Children.Add(icon);
        var time = Text(row.Time[5..].Replace('-', '/'), 12, "#667085"); time.FontFamily = new FontFamily("Cascadia Mono, Consolas, Noto Sans Mono"); ToolTip.SetTip(time, row.Time);
        Grid.SetColumn(time, 1); grid.Children.Add(time);
        var tag = Badge(category.Item1, category.Item2, category.Item3); Grid.SetColumn(tag, 2); grid.Children.Add(tag);
        var message = Text(title, 15); message.TextTrimming = TextTrimming.CharacterEllipsis; ToolTip.SetTip(message, row.Message); Grid.SetColumn(message, 3);
        if (description.Length == 0) Grid.SetColumnSpan(message, 2); else message.MaxWidth = 360;
        grid.Children.Add(message);
        if (description.Length > 0)
        {
            var detail = Text(description, 13, "#667085"); detail.TextTrimming = TextTrimming.CharacterEllipsis; detail.TextAlignment = TextAlignment.Right; ToolTip.SetTip(detail, description);
            Grid.SetColumn(detail, 4); grid.Children.Add(detail);
        }
        var info = ProxyIconButton("活动详情", "info", () => Task.CompletedTask, "#667085");
        var flyout = new Flyout { Placement = PlacementMode.LeftEdgeAlignedTop }; flyout.FlyoutPresenterClasses.Add("monitor-detail");
        var fullText = new SelectableTextBlock { Text = row.Message, FontSize = 14, Foreground = Color("#29292D"), TextWrapping = TextWrapping.Wrap };
        var content = Vertical(Text("活动详情", 15, bold: true), Divider(), Text(row.Time + "  ·  " + row.Level, 12, "#667085"), fullText); content.Width = 410;
        flyout.Content = content; info.Click += (_, _) => flyout.ShowAt(info); Grid.SetColumn(info, 5); grid.Children.Add(info); return grid;
    }
}
