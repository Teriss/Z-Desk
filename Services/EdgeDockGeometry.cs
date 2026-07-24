using System.Drawing;
using ZDesk.Models;

namespace ZDesk.Services;

public static class EdgeDockGeometry
{
    private const int RevealZonePixels = 8;

    public static bool IsCursorInRevealZone(
        DockEdge edge,
        Rectangle workingArea,
        Rectangle expandedBounds,
        Point cursor)
    {
        if (edge == DockEdge.None || workingArea.IsEmpty || expandedBounds.IsEmpty) return false;

        var horizontalStart = Math.Max(workingArea.Left, expandedBounds.Left);
        var horizontalEnd = Math.Min(workingArea.Right, expandedBounds.Right);
        var verticalStart = Math.Max(workingArea.Top, expandedBounds.Top);
        var verticalEnd = Math.Min(workingArea.Bottom, expandedBounds.Bottom);

        return edge switch
        {
            DockEdge.Left => cursor.X >= workingArea.Left &&
                cursor.X < workingArea.Left + RevealZonePixels &&
                cursor.Y >= verticalStart && cursor.Y < verticalEnd,
            DockEdge.Right => cursor.X >= workingArea.Right - RevealZonePixels &&
                cursor.X < workingArea.Right &&
                cursor.Y >= verticalStart && cursor.Y < verticalEnd,
            DockEdge.Top => cursor.Y >= workingArea.Top &&
                cursor.Y < workingArea.Top + RevealZonePixels &&
                cursor.X >= horizontalStart && cursor.X < horizontalEnd,
            _ => false
        };
    }
}
