using ZDesk.Models;

namespace ZDesk.Services;

/// <summary>Logical (DIP) sizes shared by persistence and the WPF layout chrome.</summary>
public static class LayoutSizeService
{
    public static double HeaderHeight(GroupDefinition group) => group.HasMultipleTabs ? 60 : 36;

    public static (double Width, double Height, double IconSize) ItemSize(LayoutViewMode mode, double iconTileWidth = 88) => mode switch
    {
        LayoutViewMode.ExtraLargeIcons => (132, 116, 64),
        LayoutViewMode.LargeIcons => (112, 96, 48),
        LayoutViewMode.SmallIcons or LayoutViewMode.List => (156, 32, 18),
        LayoutViewMode.Details => (156, 30, 18),
        LayoutViewMode.Tiles => (220, 64, 38),
        LayoutViewMode.Content => (156, 58, 32),
        _ => (double.IsFinite(iconTileWidth) ? Math.Clamp(iconTileWidth, 68, 112) : 88, 76, 34)
    };

    public static (double Width, double Height) MinimumExpandedSize(GroupDefinition group)
    {
        var item = ItemSize(group.ViewMode);
        // Border (2), content margin (14/17), item spacing (4), plus
        // room for WPF's list padding/rounding. Details adds its header (32).
        return (Math.Max(220, item.Width + 24),
            Math.Max(140, HeaderHeight(group) + item.Height + 26 +
                (group.ViewMode == LayoutViewMode.Details ? 32 : 0)));
    }

    public static void Normalize(GroupDefinition group)
    {
        group.Tabs ??= [];
        if (!Enum.IsDefined(group.ViewMode)) group.ViewMode = LayoutViewMode.MediumIcons;
        var minimum = MinimumExpandedSize(group);
        group.Width = double.IsFinite(group.Width) ? Math.Clamp(group.Width, minimum.Width, 1600) : GroupDefinition.DefaultWidth;
        group.Height = double.IsFinite(group.Height) ? Math.Clamp(group.Height, minimum.Height, 1200) : GroupDefinition.DefaultHeight;
    }
}
