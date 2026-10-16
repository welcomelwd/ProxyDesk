using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ProxyDesk.Core;

namespace ProxyDesk.Desktop;

internal sealed partial class Pages
{
    private Control Profiles()
    {
        var cards = new StackPanel { Spacing = 14, Margin = new Thickness(20) };
        foreach (var profile in vm.Configuration.Profiles)
        {
            var active = profile.Id == vm.Configuration.ActiveProfileId;
            var name = Horizontal(Text(profile.Name, 16, bold: true));
            if (active) name.Children.Add(Badge("已启用", "#2EBB55", "#E5F6E7"));
            var defaultColor = profile.DefaultAction == RoutingAction.Proxy ? "#1685FF" : profile.DefaultAction == RoutingAction.Direct ? "#2EBB55" : "#F05E59";
            var details = Horizontal(ProxyIcon("rules", "#667085", 14), Text($"{profile.Rules.Count} 条规则", 12, "#85858B"), Badge("默认：" + MainViewModel.ActionName(profile.DefaultAction), defaultColor,
                profile.DefaultAction == RoutingAction.Block ? "#FBE9E8" : profile.DefaultAction == RoutingAction.Proxy ? "#E1EFFF" : "#E5F6E7"));
            var information = Vertical(name, details); information.Spacing = 6;
            var content = new Grid { ColumnDefinitions = new ColumnDefinitions("72,*,Auto"), MinHeight = 58 };
            content.Children.Add(new Border { Width = 54, Height = 54, CornerRadius = new CornerRadius(27), Background = Color("#E1EFFF"),
                HorizontalAlignment = HorizontalAlignment.Left, Child = ProxyIcon("globe", size: 30) });
            Grid.SetColumn(information, 1); content.Children.Add(information);
            var menu = ProxyIconButton("配置操作 " + profile.Name, "more", () => Task.CompletedTask, "#55565D");
            var activate = MenuAction(active ? "当前配置" : "启用配置", async () => { var configuration = JsonDefaults.Clone(vm.Configuration); configuration.ActiveProfileId = profile.Id; await vm.SaveAsync(configuration); }); activate.IsEnabled = !active;
            menu.Flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight, ItemsSource = new Control[] {
                activate, MenuAction("编辑配置…", () => EditProfileAsync(profile)), MenuAction("复制配置", async () =>
                {
                    var configuration = JsonDefaults.Clone(vm.Configuration); var copy = JsonDefaults.Clone(profile) with { Id = Guid.NewGuid(), Name = profile.Name + " 副本" };
                    copy.Rules = copy.Rules.Select(rule => rule with { Id = Guid.NewGuid() }).ToList(); configuration.Profiles.Add(copy); await vm.SaveAsync(configuration);
                }), new Separator(), MenuAction("删除配置", async () =>
                {
                    var configuration = JsonDefaults.Clone(vm.Configuration);
                    if (configuration.Profiles.Count == 1) throw new InvalidOperationException("至少保留一套配置。");
                    if (configuration.Automations.Any(automation => automation.ProfileId == profile.Id)) throw new InvalidOperationException("该配置仍被自动化引用。");
                    configuration.Profiles.RemoveAll(item => item.Id == profile.Id);
                    if (configuration.ActiveProfileId == profile.Id) configuration.ActiveProfileId = configuration.Profiles[0].Id;
                    await vm.SaveAsync(configuration);
                }) } };
            Grid.SetColumn(menu, 2); content.Children.Add(menu);
            var card = new Border { Child = content, Background = Color("#FFFFFF"), CornerRadius = new CornerRadius(14), Padding = new Thickness(20, 18),
                BorderThickness = new Thickness(active ? 2 : 1), BorderBrush = Color(active ? "#83B7FF" : "#DDDEE2") };
            card.DoubleTapped += async (_, _) => await vm.RunAsync(() => EditProfileAsync(profile)); cards.Children.Add(card);
        }
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var add = ProxyIconButton("新建配置", "add", () => EditProfileAsync(), "#FFFFFF"); add.Classes.Add("primary"); add.Classes.Add("proxy-toolbar");
        Grid.SetColumn(add, 1); toolbar.Children.Add(add); return WithToolbar(toolbar, Scroll(cards));
    }

    private async Task EditProfileAsync(Profile? existing = null)
    {
        var profile = existing is null ? new Profile { Name = "" } : JsonDefaults.Clone(existing);
        var name = ProxyInput(profile.Name, "ProfileNameInput", "配置名称");
        var actions = ActionSegments(profile.DefaultAction); var proxies = vm.Configuration.Proxies.ToArray();
        var proxy = new ComboBox { ItemsSource = proxies.Select(item => item.Name).ToArray(), SelectedIndex = Array.FindIndex(proxies, item => item.Id == profile.DefaultProxyId),
            PlaceholderText = "选择代理…", HorizontalAlignment = HorizontalAlignment.Stretch };
        void UpdateProxy() { if (actions.Buttons.Any(button => button.IsChecked == true)) proxy.IsEnabled = SelectedAction(actions.Buttons) == RoutingAction.Proxy; }
        foreach (var button in actions.Buttons) button.IsCheckedChanged += (_, _) => UpdateProxy(); UpdateProxy();
        var error = new TextBlock { FontSize = 12, Foreground = Color("#D84E48"), TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var fields = Vertical(SurfaceGroup(ProxyFormRow("名称", name)), SurfaceGroup(ProxyFormRow("默认动作", actions.Control), ProxyFormRow("默认代理", proxy)),
            Text("未匹配规则的程序使用此默认动作。", 12, "#85858B"), Text($"{profile.Rules.Count} 条规则", 12, "#85858B"), error);
        await StyledEditorAsync(existing is null ? "新建配置" : "编辑配置", fields, async () =>
        {
            profile.Name = name.Text!.Trim(); profile.DefaultAction = SelectedAction(actions.Buttons);
            profile.DefaultProxyId = profile.DefaultAction == RoutingAction.Proxy ? proxies[proxy.SelectedIndex].Id : null;
            var configuration = JsonDefaults.Clone(vm.Configuration); var index = configuration.Profiles.FindIndex(item => item.Id == profile.Id);
            if (index < 0) configuration.Profiles.Add(profile); else configuration.Profiles[index] = profile;
            await vm.SaveAsync(configuration);
        }, () => actions.Buttons.Any(button => button.IsChecked == true) && !string.IsNullOrWhiteSpace(name.Text) && (SelectedAction(actions.Buttons) != RoutingAction.Proxy || proxy.SelectedIndex >= 0),
            [name, proxy, .. actions.Buttons], name, error);
    }

    private sealed record ProgramChoice(string Name, string Path, string Detail);

    private async Task<string?> ChooseProcessAsync()
    {
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process) { try { if (process.MainModule?.FileName is { } path) paths.Add(path); } catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { } }
        }
        return await ChooseProgramAsync("选择运行中的程序", paths.Select(path => new ProgramChoice(Path.GetFileNameWithoutExtension(path), path, path)).ToArray(), "play");
    }

    private async Task<string?> ChoosePackageAsync()
    {
        var applications = OperatingSystem.IsWindows() ? await Task.Run(Windows.InstalledApplications.Read) : [];
        return await ChooseProgramAsync("已安装的软件", applications.Select(application => new ProgramChoice(application.Name, application.Directory, application.Description)).ToArray(), "package", true);
    }

    private async Task<string?> ChooseInstalledApplicationAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows 开始菜单仅在 Windows 上可用。");
        var applications = await Task.Run(Windows.InstalledApplications.ReadStartMenu);
        return await ChooseProgramAsync("已安装的程序 · Windows 开始菜单", applications.Select(application =>
            new ProgramChoice(application.Name, application.Path, application.Target)).ToArray(), "app");
    }

    private async Task<string?> ChooseProgramAsync(string title, IReadOnlyList<ProgramChoice> choices, string icon, bool directoryPicker = false)
    {
        string? result = null;
        var search = ProxyInput(null, "ProgramSearchInput", "搜索名称或路径…");
        var list = new ListBox { ItemsSource = choices, Height = 364, Background = Color("#FFFFFF"), BorderThickness = new Thickness(0),
            ItemTemplate = new FuncDataTemplate<ProgramChoice>((choice, _) =>
            {
                if (choice is null) return null;
                var detail = Text(choice.Detail, 11, "#85858B"); detail.TextTrimming = TextTrimming.CharacterEllipsis;
                var text = Vertical(Text(choice.Name, 14, bold: true), detail); text.Spacing = 3;
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("38,*"), MinHeight = 52 }; row.Children.Add(ProxyIcon(icon, size: 21));
                Grid.SetColumn(text, 1); row.Children.Add(text); ToolTip.SetTip(row, choice.Path); return row;
            }) };
        list.Classes.Add("program-list");
        var count = Text($"{(directoryPicker ? "Windows" : "程序")} ({choices.Count})", 12, "#85858B");
        search.TextChanged += (_, _) =>
        {
            var rows = choices.Where(choice => (choice.Name + " " + choice.Path + " " + choice.Detail).Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToArray();
            list.ItemsSource = rows; count.Text = $"{(directoryPicker ? "Windows" : "程序")} ({rows.Length})";
        };
        var error = new TextBlock { IsVisible = false, Foreground = Color("#D84E48"), TextWrapping = TextWrapping.Wrap };
        var fields = Vertical(search, count, list, error); fields.Spacing = 10;
        if (directoryPicker)
        {
            fields.Children.Add(Text("显示提供安装目录的软件；选择后可为该目录添加规则。", 11, "#85858B"));
            fields.Children.Add(CompactButton("选择其他目录…", "folder", async () =>
            {
                var folders = await DialogOwner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择程序目录", AllowMultiple = false });
                if (folders.Count > 0) { result = folders[0].Path.LocalPath; DialogOwner.Close(); }
            }));
        }
        await StyledEditorAsync(title, fields, () => { result = ((ProgramChoice)list.SelectedItem!).Path; return Task.CompletedTask; },
            () => list.SelectedItem is ProgramChoice, [list], search, error, "选择", 650);
        return result;
    }
}
