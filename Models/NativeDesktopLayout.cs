namespace ZDesk.Models;

/// <summary>
/// Geometry shared by native desktop presentation hosts. It intentionally has
/// no WPF dependency so a top-level Win32 window can replace HwndHost without
/// changing item placement, scrolling, or hit testing semantics.
/// </summary>
internal static class NativeDesktopLayout
{
    public const int HeaderHeight = 40;

    public static NativeDesktopRect GetItemRect(
        DesktopPresentationSnapshot snapshot,
        int index,
        int clientWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        var iconView = snapshot.ViewMode is LayoutViewMode.ExtraLargeIcons or LayoutViewMode.LargeIcons or
            LayoutViewMode.MediumIcons or LayoutViewMode.SmallIcons or LayoutViewMode.Tiles;
        if (!iconView)
        {
            var height = snapshot.ViewMode == LayoutViewMode.Content ? 58 : snapshot.ViewMode == LayoutViewMode.Details ? 30 : 32;
            var top = HeaderHeight + index * height;
            return new NativeDesktopRect(6, top, Math.Max(120, clientWidth - 6), top + height - 2);
        }

        var width = snapshot.ViewMode switch
        {
            LayoutViewMode.ExtraLargeIcons => 132,
            LayoutViewMode.LargeIcons => 112,
            LayoutViewMode.SmallIcons => 156,
            LayoutViewMode.Tiles => 220,
            _ => 96
        };
        var heightForView = snapshot.ViewMode switch
        {
            LayoutViewMode.ExtraLargeIcons => 116,
            LayoutViewMode.LargeIcons => 96,
            LayoutViewMode.Tiles => 64,
            LayoutViewMode.SmallIcons => 32,
            _ => 76
        };
        var columns = Math.Max(1, clientWidth / width);
        var column = index % columns;
        var row = index / columns;
        var left = 6 + column * width;
        var topForView = HeaderHeight + row * heightForView;
        return new NativeDesktopRect(left, topForView, left + width - 4, topForView + heightForView - 2);
    }

    public static int HitTestIndex(DesktopPresentationSnapshot snapshot, int x, int y, int clientWidth)
    {
        for (var index = 0; index < snapshot.Items.Count; index++)
        {
            if (GetItemRect(snapshot, index, clientWidth).Contains(x, y)) return index;
        }
        return -1;
    }

    public static int GetContentHeight(DesktopPresentationSnapshot snapshot, int clientWidth)
    {
        if (snapshot.Items.Count == 0) return HeaderHeight;
        return GetItemRect(snapshot, snapshot.Items.Count - 1, clientWidth).Bottom + 4;
    }

    public static int ClampScrollOffset(DesktopPresentationSnapshot snapshot, int requestedOffset, int clientWidth, int clientHeight) =>
        Math.Clamp(requestedOffset, 0, Math.Max(0, GetContentHeight(snapshot, clientWidth) - clientHeight));
}

internal readonly record struct NativeDesktopRect(int Left, int Top, int Right, int Bottom)
{
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}
