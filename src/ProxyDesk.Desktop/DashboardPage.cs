using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using ProxyDesk.Core;

namespace ProxyDesk.Desktop;

internal sealed partial class Pages
{
    private Control Dashboard()
    {
        var toggle = Button(vm.ProxyActionText, vm.ToggleProxyAsync); toggle.Classes.Add("proxy-toolbar");
        toggle.Bind(ContentControl.ContentProperty, new Binding(nameof(vm.ProxyActionText)) { Source = vm });
        toggle.Bind(Control.IsEnabledProperty, new Binding(nameof(vm.CanToggleProxy)) { Source = vm });
        var symbol = Text("", 32, "#2EBB55", true); symbol.HorizontalAlignment = HorizontalAlignment.Center;
        symbol.Bind(TextBlock.TextProperty, new Binding(nameof(vm.RunningText)) { Source = vm,
            Converter = new FuncValueConverter<string, string>(value => value == "代理已启用" ? "✓" : "◉") });
        symbol.Bind(TextBlock.ForegroundProperty, new Binding(nameof(vm.RunningText)) { Source = vm,
            Converter = new FuncValueConverter<string, IBrush>(value => Color(value == "代理已启用" ? "#2EBB55" : "#85858B")) });
        var icon = new Border { Width = 62, Height = 62, CornerRadius = new CornerRadius(16), Child = symbol };
        icon.Bind(Border.BackgroundProperty, new Binding(nameof(vm.RunningText)) { Source = vm,
            Converter = new FuncValueConverter<string, IBrush>(value => Color(value == "代理已启用" ? "#CBE9C9" : "#E4E8EE")) });
        var explanation = Text("", 12, "#85858B");
        explanation.Bind(TextBlock.TextProperty, new Binding(nameof(vm.RunningText)) { Source = vm, Converter = new FuncValueConverter<string, string>(value => value switch
        {
            "代理已启用" => "应用流量按照当前配置中的规则处理。",
            "后台未连接" => "请在设置中检查并启动后台服务。",
            _ => "启用后，新建连接将按照当前配置处理。"
        }) });
        var status = Vertical(Live(nameof(vm.RunningText), 19, bold: true), explanation); status.Spacing = 5;
        var hero = new Grid { ColumnDefinitions = new ColumnDefinitions("84,*,Auto"), MinHeight = 78 };
        hero.Children.Add(icon); Grid.SetColumn(status, 1); hero.Children.Add(status); Grid.SetColumn(toggle, 2); hero.Children.Add(toggle);
        var banner = Card(hero);

        var summaries = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 14 };
        var profileName = Horizontal(Live(nameof(vm.ProfileName), 17, bold: true), Badge("当前配置", "#2EBB55", "#E5F6E7"));
        var defaultColor = vm.Configuration.ActiveProfile.DefaultAction switch { RoutingAction.Proxy => "#1685FF", RoutingAction.Block => "#F05E59", _ => "#2EBB55" };
        var defaultBackground = vm.Configuration.ActiveProfile.DefaultAction switch { RoutingAction.Proxy => "#E1EFFF", RoutingAction.Block => "#FBE9E8", _ => "#E5F6E7" };
        var profile = Vertical(Horizontal(ProxyIcon("globe", size: 21), profileName),
            Horizontal(Badge("默认：" + vm.DefaultAction, defaultColor, defaultBackground), Badge(vm.RuleCount + " 条规则", "#AF55D5", "#F3E5F9")),
            Horizontal(ProxyIcon("wifi", "#85858B", 14), Live(nameof(vm.SsidText), 12, "#85858B")));
        var automation = Vertical(Horizontal(ProxyIcon("wifi", size: 21), Text("自动化", 15, bold: true)),
            Horizontal(Badge(vm.Configuration.Settings.AutomationEnabled ? "已开启" : "已关闭", vm.Configuration.Settings.AutomationEnabled ? "#2EBB55" : "#85858B",
                vm.Configuration.Settings.AutomationEnabled ? "#E5F6E7" : "#E4E8EE"), Live(nameof(vm.AutomationCount), 12, "#85858B")),
            CompactButton("管理自动化", "edit", () => { owner.Navigate("自动化"); return Task.CompletedTask; }));
        var runtime = Vertical(Horizontal(ProxyIcon("activity", size: 21), Text("运行状态", 15, bold: true)),
            Live(nameof(vm.Runtime), 25, bold: true), Horizontal(
                Horizontal(Text("连接记录", 11, "#85858B"), Live(nameof(vm.ConnectionCount), 16, "#1685FF", true)),
                Horizontal(Text("应用", 11, "#85858B"), Live(nameof(vm.AppCount), 16, "#1685FF", true))));
        var summaryCards = new[] { Card(profile), Card(automation), Card(runtime) };
        for (var index = 0; index < summaryCards.Length; index++)
        {
            summaryCards[index].MinHeight = 164; Grid.SetColumn(summaryCards[index], index); summaries.Children.Add(summaryCards[index]);
        }

        var counts = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 14 };
        var items = new[] { ("代理", nameof(vm.ProxyCount), "server"), ("规则", nameof(vm.RuleCount), "rules"), ("配置", nameof(vm.ProfileCount), "folder") };
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            var add = ProxyIconButton("新建" + item.Item1, "add", () => item.Item1 == "代理" ? EditProxyAsync() : item.Item1 == "规则" ? EditRuleAsync() : EditProfileAsync());
            var countIcon = ProxyIcon(item.Item3, size: 21); countIcon.HorizontalAlignment = HorizontalAlignment.Left;
            var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") }; top.Children.Add(countIcon); Grid.SetColumn(add, 1); top.Children.Add(add);
            var card = Card(Vertical(top, Live(item.Item2, 31, bold: true), Text(item.Item1, 12, "#85858B")));
            Grid.SetColumn(card, index); counts.Children.Add(card);
        }
        var network = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        network.Children.Add(Horizontal(ProxyIcon("info", size: 17), Live(nameof(vm.NetworkStatus), 12), Live(nameof(vm.BackendPermission), 12, "#85858B")));
        var settings = CompactButton("组件状态", "info", () => { owner.Navigate("设置"); return Task.CompletedTask; }); Grid.SetColumn(settings, 1); network.Children.Add(settings);
        var body = Vertical(banner, summaries, counts, Card(network)); body.Spacing = 18;
        return Scroll(Padded(body));
    }
}
