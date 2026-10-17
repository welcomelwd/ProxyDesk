using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ProxyDesk.Core;
using ProxyDesk.Platform.Windows;

namespace ProxyDesk.Desktop;

internal sealed partial class Pages
{
    private Button CompactButton(string label, string icon, Func<Task> action)
    {
        var button = Button(label, action); button.Classes.Add("proxy-toolbar");
        button.Content = Horizontal(ProxyIcon(icon, "#77797E", 13), Text(label, 12)); return button;
    }

    private MenuItem MenuAction(string label, Func<Task> action)
    {
        var item = new MenuItem { Header = label }; item.Click += async (_, _) => await vm.RunAsync(action); return item;
    }

    private static Control Badge(string label, string foreground = "#1685FF", string background = "#E1EFFF") =>
        new Border { Child = Text(label, 11, foreground), Background = Color(background), CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };

    private Control Rules()
    {
        var table = new DataGrid { ItemsSource = vm.Rules, AutoGenerateColumns = false, IsReadOnly = true, RowHeight = 70,
            ColumnHeaderHeight = 36, RowBackground = Brushes.Transparent, GridLinesVisibility = DataGridGridLinesVisibility.None,
            CanUserReorderColumns = false, SelectionMode = DataGridSelectionMode.Single, Background = Brushes.Transparent };
        table.Classes.Add("proxy-table");
        void Column(string label, double width, Func<RuleRow, Control> content, bool pixels = false) => table.Columns.Add(
            new DataGridTemplateColumn { Header = label, Width = new DataGridLength(width, pixels ? DataGridLengthUnitType.Pixel : DataGridLengthUnitType.Star),
                CellTemplate = new FuncDataTemplate<RuleRow>((row, _) => row is null ? null : content(row)) });
        Column("已启用", 88, row =>
        {
            var toggle = new ToggleButton { IsChecked = row.Enabled, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            toggle.Classes.Add("proxy-switch"); AutomationProperties.SetName(toggle, "启用规则 " + row.Application);
            toggle.Click += async (_, _) =>
            {
                toggle.IsEnabled = false;
                try { await vm.ToggleRuleAsync(row.Id, toggle.IsChecked == true); }
                catch (Exception error) { toggle.IsChecked = row.Enabled; vm.Error = error.Message; }
                finally { toggle.IsEnabled = true; }
            };
            return toggle;
        }, true);
        Column("应用", 2.3, row =>
        {
            var avatar = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Background = Color("#E1EFFF"),
                Child = row.Kind == "目录" ? ProxyIcon("folder", size: 21) : Text(row.Application[..1].ToUpperInvariant(), 18, "#1685FF", true) };
            avatar.Child.HorizontalAlignment = HorizontalAlignment.Center;
            var name = Text(row.Application, bold: true); name.TextTrimming = TextTrimming.CharacterEllipsis;
            var path = Text(row.Path, 11, "#85858B"); path.TextTrimming = TextTrimming.CharacterEllipsis; ToolTip.SetTip(path, row.Path);
            var details = Vertical(name, path); details.Spacing = 3;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("46,*"), VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(avatar); Grid.SetColumn(details, 1); grid.Children.Add(details); return grid;
        });
        Column("类型", .7, row => Badge(row.Kind));
        Column("动作", .8, row => ProxyStatus(row.Action, row.Action == "代理" ? "#1685FF" : row.Action == "直连" ? "#34C759" : "#F05E59"));
        Column("代理", 1, row => Text(row.Proxy, 13, "#85858B"));
        Column("最近匹配", 1, row => ProxyStatus(row.LastMatch, row.LastMatch == "—" ? "#A4A4A8" : "#34C759"));
        Column("操作", 126, row =>
        {
            ApplicationRule Rule() => vm.Configuration.ActiveProfile.Rules.Single(rule => rule.Id == row.Id);
            var more = ProxyIconButton("更多规则操作", "more", () => Task.CompletedTask, "#85858B");
            more.Flyout = new MenuFlyout { ItemsSource = new Control[] {
                MenuAction("上移", () => MoveRuleAsync(row.Id, -1)), MenuAction("下移", () => MoveRuleAsync(row.Id, 1)) } };
            var actions = Horizontal(more, ProxyIconButton("编辑规则", "edit", () => EditRuleAsync(Rule()), "#85858B"),
                ProxyIconButton("删除规则", "delete", async () =>
                {
                    var configuration = JsonDefaults.Clone(vm.Configuration); configuration.ActiveProfile.Rules.RemoveAll(rule => rule.Id == row.Id); await vm.SaveAsync(configuration);
                }, "#F05E59")); actions.Spacing = 5; return actions;
        }, true);
        table.DoubleTapped += async (_, _) =>
        {
            if (table.SelectedItem is RuleRow row) await vm.RunAsync(() => EditRuleAsync(vm.Configuration.ActiveProfile.Rules.Single(rule => rule.Id == row.Id)));
        };
        var stripes = new ProxyTableBackdrop { RowHeight = 70 }; stripes.Bind(ProxyTableBackdrop.RowCountProperty, new Binding("Count") { Source = vm.Rules });
        var body = new Grid { Background = Color("#FFFFFF") }; body.Children.Add(stripes); body.Children.Add(table);
        var profiles = new ComboBox { ItemsSource = vm.Configuration.Profiles, SelectedItem = vm.Configuration.ActiveProfile, Width = 190, MinHeight = 26,
            ItemTemplate = new FuncDataTemplate<Profile>((profile, _) => profile is null ? null : Horizontal(ProxyIcon("globe", size: 15), Text(profile.Name, 13))) };
        profiles.Classes.Add("rule-profile-picker");
        profiles.SelectionChanged += async (_, _) =>
        {
            if (profiles.SelectedItem is Profile selected && selected.Id != vm.Configuration.ActiveProfileId)
                await vm.RunAsync(async () => { var configuration = JsonDefaults.Clone(vm.Configuration); configuration.ActiveProfileId = selected.Id; await vm.SaveAsync(configuration); });
        };
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") }; toolbar.Children.Add(profiles);
        var add = ProxyIconButton("添加规则", "add", () => EditRuleAsync(), "#FFFFFF"); add.Classes.Add("primary"); add.Classes.Add("proxy-toolbar");
        var tools = Horizontal(CompactButton("导入", "import", ImportRulesAsync), CompactButton("导出", "export", ExportRulesAsync),
            CompactButton("软件包", "package", async () =>
            {
                var selected = await ChoosePackageAsync();
                if (selected is not null) await EditRuleAsync(seed: new() { Name = Path.GetFileName(selected.TrimEnd(Path.DirectorySeparatorChar)),
                    Selector = new() { Kind = SelectorKind.DirectoryGlob, Value = selected }, Action = RoutingAction.Direct });
            }), add);
        tools.Spacing = 10; Grid.SetColumn(tools, 1); toolbar.Children.Add(tools); return WithToolbar(toolbar, body);
    }

    private async Task MoveRuleAsync(Guid id, int direction)
    {
        var configuration = JsonDefaults.Clone(vm.Configuration); var rules = configuration.ActiveProfile.Rules;
        var index = rules.FindIndex(rule => rule.Id == id); var next = index + direction;
        if (next < 0 || next >= rules.Count) return;
        (rules[index], rules[next]) = (rules[next], rules[index]); await vm.SaveAsync(configuration);
    }

    private static (Grid Control, RadioButton[] Buttons) ActionSegments(RoutingAction value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), Width = 180, HorizontalAlignment = HorizontalAlignment.Right, Background = Color("#DDDEE2") };
        var choices = new[] { ("直连", RoutingAction.Direct), ("代理", RoutingAction.Proxy), ("阻止", RoutingAction.Block) };
        var buttons = new RadioButton[3]; var group = Guid.NewGuid().ToString();
        for (var index = 0; index < choices.Length; index++)
        {
            var button = new RadioButton { Content = choices[index].Item1, Tag = choices[index].Item2, IsChecked = choices[index].Item2 == value, GroupName = group };
            button.Classes.Add("proxy-segment"); buttons[index] = button; Grid.SetColumn(button, index); grid.Children.Add(button);
        }
        return (grid, buttons);
    }

    private static RoutingAction SelectedAction(RadioButton[] buttons) => (RoutingAction)buttons.First(button => button.IsChecked == true).Tag!;

    private async Task EditRuleAsync(ApplicationRule? existing = null, ApplicationRule? seed = null)
    {
        var sourceProfileId = vm.Configuration.ActiveProfileId;
        var rule = JsonDefaults.Clone(existing ?? seed ?? new ApplicationRule { Action = vm.Configuration.Proxies.Count > 0 ? RoutingAction.Proxy : RoutingAction.Direct });
        var path = ProxyInput(rule.Selector.Value, "RulePathInput", OperatingSystem.IsWindows() ? @"C:\Program Files\Example\Example.exe" : "/usr/bin/example");
        var name = ProxyInput(rule.Name, "RuleNameInput", "留空时使用程序名称");
        var kind = new ComboBox { ItemsSource = new[] { "程序名", "完整路径", "目录通配符", "脚本路径" }, SelectedIndex = (int)rule.Selector.Kind, HorizontalAlignment = HorizontalAlignment.Stretch };
        var platform = new ComboBox { ItemsSource = new[] { "Windows", "Linux" }, SelectedIndex = (int)rule.Selector.Platform, HorizontalAlignment = HorizontalAlignment.Stretch };
        var enabled = new ToggleButton { IsChecked = rule.Enabled, HorizontalAlignment = HorizontalAlignment.Right }; enabled.Classes.Add("proxy-switch"); AutomationProperties.SetName(enabled, "规则启用状态");
        var error = new TextBlock { Foreground = Color("#D84E48"), FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        string? inferredName = null;
        void SelectPath(string value)
        {
            if (OperatingSystem.IsWindows())
            {
                var target = WindowsRuleTargets.Resolve(value);
                path.Text = target.Path; kind.SelectedIndex = (int)target.Kind;
                if (string.IsNullOrWhiteSpace(name.Text) || name.Text == inferredName) { name.Text = target.Name; inferredName = target.Name; }
                error.IsVisible = false; return;
            }
            var directory = Directory.Exists(value);
            path.Text = value; kind.SelectedIndex = (int)(directory ? SelectorKind.DirectoryGlob : SelectorKind.FullPath); error.IsVisible = false;
        }
        async Task BrowseFile()
        {
            var files = await DialogOwner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择程序", AllowMultiple = false,
                FileTypeFilter = OperatingSystem.IsWindows() ? [new FilePickerFileType("程序、工具、脚本和快捷方式") {
                    Patterns = ["*.exe", "*.com", "*.lnk", .. WindowsRuleTargets.ScriptExtensions.Select(extension => "*" + extension)] }, FilePickerFileTypes.All] : [FilePickerFileTypes.All] });
            if (files.Count > 0) SelectPath(files[0].Path.LocalPath);
        }
        var dropContent = Vertical(ProxyIcon("import", "#8E8E95", 34), Text("拖入程序、工具、脚本、文件夹或快捷方式", 13, "#85858B"));
        dropContent.HorizontalAlignment = HorizontalAlignment.Center; dropContent.Spacing = 10;
        var dropButton = Button("选择程序", BrowseFile); dropButton.Content = dropContent; dropButton.Classes.Add("flat"); dropButton.HorizontalAlignment = HorizontalAlignment.Stretch; dropButton.Height = 118;
        var dropZone = new Grid { Height = 118 };
        dropZone.Children.Add(new Avalonia.Controls.Shapes.Rectangle { Stroke = Color("#C8C8CC"), StrokeThickness = 2, StrokeDashArray = new AvaloniaList<double> { 4, 3 }, RadiusX = 10, RadiusY = 10, IsHitTestVisible = false });
        dropZone.Children.Add(dropButton); DragDrop.SetAllowDrop(dropZone, true);
        dropZone.AddHandler(DragDrop.DragOverEvent, (_, args) => args.DragEffects = args.DataTransfer.TryGetFiles()?.Any() == true ? DragDropEffects.Copy : DragDropEffects.None);
        dropZone.AddHandler(DragDrop.DropEvent, (_, args) =>
        {
            try { if (args.DataTransfer.TryGetFiles()?.FirstOrDefault() is { } item) SelectPath(item.Path.LocalPath); }
            catch (Exception failure) { error.Text = failure.Message; error.IsVisible = true; }
        });
        var browse = ProxyIconButton("选择程序", "folder", BrowseFile, "#77797E"); browse.Classes.Add("proxy-toolbar");
        var pathLine = new Grid { ColumnDefinitions = new ColumnDefinitions("44,*,34"), ColumnSpacing = 8 };
        pathLine.Children.Add(Text("路径")); Grid.SetColumn(path, 1); pathLine.Children.Add(path); Grid.SetColumn(browse, 2); pathLine.Children.Add(browse);
        var running = CompactButton("运行中的程序…", "play", async () => { var selected = await ChooseProcessAsync(); if (selected is not null) SelectPath(selected); });
        var installed = CompactButton("已安装的程序…", "app", async () => { var selected = await ChooseInstalledApplicationAsync(); if (selected is not null) SelectPath(selected); });
        installed.IsVisible = OperatingSystem.IsWindows();
        var pathSection = Vertical(pathLine, Text("支持程序、脚本完整路径、目录通配符；快捷方式自动识别目标。", 11, "#85858B"), Horizontal(running, installed)); pathSection.Spacing = 6;
        var actions = ActionSegments(rule.Action);
        var descendants = MonitorSwitch("同时应用于派生进程", rule.IncludeDescendants);
        descendants.HorizontalAlignment = HorizontalAlignment.Right;
        ToolTip.SetTip(descendants, "按观察到的父子进程关系继承规则。启动后立即退出的启动器可能无法追踪，可为目标程序单独添加规则。");
        var dns = MonitorSwitch("DNS 解析走代理", rule.StrictDns);
        dns.HorizontalAlignment = HorizontalAlignment.Right;
        ToolTip.SetTip(dns, "开启：仅将应用自己发出的普通 DNS 查询经代理转发；系统代发查询仍使用系统 DNS。关闭：普通 DNS 使用系统解析。此功能不向应用加载 DLL。");
        var proxies = vm.Configuration.Proxies.ToArray();
        var proxy = new ComboBox { ItemsSource = proxies.Select(item => item.Name).ToArray(), SelectedIndex = Array.FindIndex(proxies, item => item.Id == rule.ProxyId),
            PlaceholderText = "选择代理…", MinWidth = 190, HorizontalAlignment = HorizontalAlignment.Right };
        void UpdateProxy() { var isProxy = SelectedAction(actions.Buttons) == RoutingAction.Proxy; proxy.IsEnabled = isProxy; dns.IsEnabled = isProxy; }
        foreach (var button in actions.Buttons) button.IsCheckedChanged += (_, _) => { if (actions.Buttons.Any(item => item.IsChecked == true)) UpdateProxy(); };
        UpdateProxy();
        var advanced = new Expander { Header = "高级选项", IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = Vertical(ProxyFormRow("名称", name), ProxyFormRow("匹配方式", kind), ProxyFormRow("所属平台", platform), ProxyFormRow("启用规则", enabled),
                Text("脚本按实际执行的完整路径匹配。规则按列表顺序匹配，首条生效。", 11, "#85858B")) };
        advanced.Classes.Add("rule-advanced");
        var profileChecks = new Dictionary<Guid, CheckBox>(); var targetRows = new StackPanel { Spacing = 8 };
        targetRows.Children.Add(Text(existing is null ? "添加到配置" : "所在配置", 14)); targetRows.Children.Add(Divider());
        foreach (var profile in vm.Configuration.Profiles)
        {
            if (existing is not null && profile.Id != sourceProfileId) continue;
            var label = Horizontal(ProxyIcon("globe", size: 14), Text(profile.Name, 13));
            if (profile.Id == vm.Configuration.ActiveProfileId) label.Children.Add(Badge("已启用", "#2EBB55", "#E5F6E7"));
            var check = new CheckBox { Content = label, IsChecked = profile.Id == sourceProfileId, IsEnabled = existing is null, MinHeight = 28 };
            profileChecks.Add(profile.Id, check); targetRows.Children.Add(check);
        }
        var fields = Vertical(dropZone, pathSection, SurfaceGroup(ProxyFormRow("动作", actions.Control), ProxyFormRow("代理服务器", proxy),
            ProxyFormRow("同时应用于派生进程", descendants), ProxyFormRow("DNS 解析走代理", dns),
            Text("仅支持归属明确的应用 DNS；Windows 系统代发查询保持系统解析。", 11, "#85858B")),
            SurfaceGroup(advanced), new Border { Child = targetRows, Background = Color("#FFFFFF"), Padding = new Thickness(14), CornerRadius = new CornerRadius(9) }, error);
        await StyledEditorAsync(existing is null ? "添加规则" : "编辑规则", fields, async () =>
        {
            rule.Selector.Value = path.Text!.Trim(); rule.Selector.Kind = (SelectorKind)kind.SelectedIndex; rule.Selector.Platform = (AppPlatform)platform.SelectedIndex;
            rule.Name = string.IsNullOrWhiteSpace(name.Text) ? Path.GetFileNameWithoutExtension(rule.Selector.Value.TrimEnd('\\', '/')) : name.Text.Trim();
            rule.Action = SelectedAction(actions.Buttons); rule.ProxyId = rule.Action == RoutingAction.Proxy ? proxies[proxy.SelectedIndex].Id : null;
            rule.Enabled = enabled.IsChecked == true; rule.StrictDns = dns.IsChecked == true; rule.IncludeDescendants = descendants.IsChecked == true;
            var configuration = JsonDefaults.Clone(vm.Configuration);
            foreach (var id in profileChecks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key))
            {
                var rules = configuration.Profiles.Single(profile => profile.Id == id).Rules;
                var index = rules.FindIndex(item => item.Id == rule.Id);
                var saved = existing is null && id != sourceProfileId ? rule with { Id = Guid.NewGuid() } : rule;
                if (index < 0) rules.Add(saved); else rules[index] = saved;
            }
            await vm.SaveAsync(configuration);
        }, () => actions.Buttons.Any(button => button.IsChecked == true) && !string.IsNullOrWhiteSpace(path.Text) && (SelectedAction(actions.Buttons) != RoutingAction.Proxy || proxy.SelectedIndex >= 0) && profileChecks.Any(pair => pair.Value.IsChecked == true),
            [path, proxy, .. actions.Buttons, .. profileChecks.Values], path, error, existing is null ? "创建规则" : "保存", 650);
    }

    private static Border SurfaceGroup(params Control[] rows)
    {
        var group = ProxyFormGroup(rows); group.Background = Color("#FFFFFF"); group.BorderThickness = new Thickness(0); group.CornerRadius = new CornerRadius(9); return group;
    }

    private async Task StyledEditorAsync(string title, Control content, Func<Task> save, Func<bool> valid, IEnumerable<Control> watched,
        Control focus, TextBlock error, string submitLabel = "保存", double width = 500, bool showCancel = true)
    {
        var dialog = new Window { Title = title, Width = width, MaxHeight = Math.Min(owner.Bounds.Height - 72, 760), SizeToContent = SizeToContent.Height, CanResize = false,
            ShowInTaskbar = false, WindowDecorations = WindowDecorations.None, Background = Brushes.Transparent, TransparencyLevelHint = [WindowTransparencyLevel.Transparent], WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var submit = new Button { Content = submitLabel, IsDefault = true }; submit.Classes.Add("primary"); submit.Classes.Add("proxy-toolbar");
        var saving = false; void Validate() => submit.IsEnabled = !saving && valid();
        foreach (var control in watched)
        {
            if (control is TextBox text) text.TextChanged += (_, _) => Validate();
            else if (control is SelectingItemsControl selection) selection.SelectionChanged += (_, _) => Validate();
            else if (control is ToggleButton toggle) toggle.IsCheckedChanged += (_, _) => Validate();
        }
        Validate();
        submit.Click += async (_, _) =>
        {
            saving = true; Validate(); error.IsVisible = false;
            try { await save(); dialog.Close(); }
            catch (Exception failure) { error.Text = failure.GetBaseException().Message; error.IsVisible = true; }
            finally { saving = false; Validate(); }
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 58, Margin = new Thickness(20, 0) }; header.Children.Add(Text(title, 15, bold: true));
        var close = ProxyIconButton("关闭", "close", () => { dialog.Close(); return Task.CompletedTask; }, "#85858B"); Grid.SetColumn(close, 1); header.Children.Add(close);
        var cancel = new Button { Content = "取消", IsCancel = true, IsVisible = showCancel, HorizontalAlignment = HorizontalAlignment.Left }; cancel.Classes.Add("proxy-toolbar"); cancel.Click += (_, _) => dialog.Close();
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(20, 16) }; footer.Children.Add(cancel); Grid.SetColumn(submit, 1); footer.Children.Add(submit);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") }; layout.Children.Add(header);
        var body = Scroll(new Border { Child = content, Padding = new Thickness(24, 8, 24, 22) }); Grid.SetRow(body, 1); layout.Children.Add(body);
        var bottom = new Border { Child = footer, BorderBrush = Color("#D1D1D4"), BorderThickness = new Thickness(0, 1, 0, 0) }; Grid.SetRow(bottom, 2); layout.Children.Add(bottom);
        dialog.Content = new Border { Child = layout, Background = Color("#EEEEEF"), CornerRadius = new CornerRadius(12), BorderBrush = Color("#C8C8CC"), BorderThickness = new Thickness(1), ClipToBounds = true };
        dialog.Opened += (_, _) => focus.Focus();
        var previous = currentForm; var parent = DialogOwner; currentForm = dialog;
        var scrim = owner.FindControl<Border>("DialogScrim")!; var previousScrim = scrim.IsVisible; scrim.IsVisible = true;
        try { await dialog.ShowDialog(parent); }
        finally { currentForm = previous; scrim.IsVisible = previousScrim; }
    }
}
