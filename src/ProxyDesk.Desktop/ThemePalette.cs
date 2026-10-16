using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace ProxyDesk.Desktop;

internal static class ThemePalette
{
    public static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
    public static IBrush Brush(string light)
    {
        var value = light.ToUpperInvariant() switch
        {
            "#29292D" or "#252528" => "#20242C",
            "#85858B" or "#838387" or "#797A83" or "#85868E" or "#77797E" or "#77797C" or "#797A80" or "#8A8B90" or
            "#8B8D94" or "#8A8B92" or "#8A8B93" or "#929296" or "#8E8E95" or "#9599A3" or "#96979E" or "#A4A4A8" or "#55565D" => "#667085",
            "#1685FF" or "#0A88FF" or "#258AFF" or "#1888FF" or "#259FE0" => "#1769D2",
            "#2EBB55" or "#34C759" or "#23B946" or "#2BB65A" or "#26B746" or "#24C445" => "#237A45",
            "#AF55D5" or "#A34FD3" or "#BB59DC" or "#AD55D7" or "#7764D8" => "#7952B3",
            "#E8901E" or "#F59835" => "#A55E12",
            "#F05E59" or "#D84E48" => "#C83E48",
            "#EEEEEF" => "#F7F8FA",
            "#E4E4E7" => "#F0F2F5",
            "#E9E9EB" => "#F3F5F8",
            "#F4F5F5" => "#F6F7F9",
            "#E1EFFF" => "#EAF2FE",
            "#E5F6E7" or "#CBE9C9" or "#E1ECE3" => "#EAF6EF",
            "#F3E5F9" => "#F1EDFA",
            "#FBE9E8" => "#FDEEF0",
            "#FFF1DF" => "#FFF3E6",
            "#E4E8EE" or "#E0E7EE" or "#E0E7EF" or "#E4EAEC" or "#E5E3ED" or "#EAE1EC" or "#EDE7DF" => "#F4F6F9",
            "#D1D1D4" or "#DDDEE2" or "#C8C8CC" => "#DCE2EA",
            "#E6E6E8" or "#E3E8EF" => "#E7EBF0",
            "#FCFCFD" => "#FFFFFF",
            _ => light
        };
        if (!IsDark) return Avalonia.Media.Brush.Parse(value);
        return Avalonia.Media.Brush.Parse(value.ToUpperInvariant() switch
        {
            "#20242C" => "#ECF0F6",
            "#667085" => "#A8B3C5",
            "#1769D2" => "#75AAFF",
            "#237A45" => "#78C99A",
            "#7952B3" => "#BEA3E7",
            "#A55E12" => "#E8B16B",
            "#C83E48" => "#F38B94",
            "#FFFFFF" => "#232A35",
            "#F7F8FA" => "#191F28",
            "#F0F2F5" => "#1F2630",
            "#F3F5F8" or "#F6F7F9" or "#F4F6F9" => "#29313D",
            "#EAF2FE" => "#283D59",
            "#EAF6EF" => "#263F33",
            "#F1EDFA" => "#3B304E",
            "#FDEEF0" => "#482F37",
            "#FFF3E6" or "#FFFDF9" => "#423729",
            "#DCE2EA" or "#E7EBF0" => "#384352",
            "#E0F2F0" => "#27423F",
            _ => value
        });
    }
    public static void RefreshResources()
    {
        var resources = Application.Current!.Resources;
        resources["WindowBackground"] = Brush("#EEEEEF");
        resources["SidebarBackground"] = Brush("#E4E4E7");
        resources["SurfaceBackground"] = Brush("#FFFFFF");
        resources["PrimaryForeground"] = Brush("#252528");
        resources["TableBorder"] = Brush("#E6E6E8");
        resources["SecondaryForeground"] = Brush("#667085");
        resources["AccentForeground"] = Brush("#1769D2");
        resources["SoftAccentBackground"] = Brush("#EAF2FE");
        resources["SurfaceBorder"] = Brush("#DCE2EA");
    }
}
