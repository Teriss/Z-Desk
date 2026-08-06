using System.Drawing;
using ZDesk.Models;

namespace ZDesk.Services;

public static class QrRecognitionFrameGeometry
{
    public const int MinimumWidthDip = 240;
    public const int MinimumHeightDip = 160;
    public const int DefaultWidthDip = 720;
    public const int DefaultHeightDip = 480;

    public static Rectangle CreateDefault(Point cursor, IReadOnlyList<Rectangle> displays)
        => CreateDefault(cursor, displays, DefaultWidthDip, DefaultHeightDip);

    public static Rectangle CreateDefault(Point cursor, IReadOnlyList<Rectangle> displays, int width, int height)
    {
        var display = displays.FirstOrDefault(item => item.Contains(cursor));
        if (display.IsEmpty) display = displays.FirstOrDefault();
        if (display.IsEmpty) return new Rectangle(cursor.X - width / 2, cursor.Y - height / 2, width, height);
        width = Math.Min(width, display.Width);
        height = Math.Min(height, display.Height);
        return new Rectangle(display.Left + (display.Width - width) / 2, display.Top + (display.Height - height) / 2, width, height);
    }

    public static Rectangle ClampToWorkArea(Rectangle frame, IReadOnlyList<Rectangle> workAreas, Point cursor, int headerHeightPixels = 0)
    {
        if (workAreas.Count == 0) return frame;
        var visible = workAreas.Any(area => Rectangle.Intersect(area, frame).Width >= 32 && Rectangle.Intersect(area, frame).Height >= 32);
        if (!visible)
        {
            var created = CreateDefault(cursor, workAreas);
            return ClampToWorkArea(created, workAreas, cursor, headerHeightPixels);
        }
        var best = workAreas.OrderByDescending(area => Rectangle.Intersect(area, frame).Width * Rectangle.Intersect(area, frame).Height).First();
        var width = Math.Min(Math.Max(frame.Width, MinimumWidthDip), Math.Max(MinimumWidthDip, best.Width));
        var height = Math.Min(Math.Max(frame.Height, MinimumHeightDip), Math.Max(MinimumHeightDip, best.Height));
        var left = Math.Clamp(frame.Left, best.Left - width + 32, best.Right - 32);
        var top = Math.Clamp(frame.Top, best.Top + headerHeightPixels, best.Bottom - 32);
        return new Rectangle(left, top, width, height);
    }

    public static Rectangle SelectTargetWorkArea(Rectangle frame, IReadOnlyList<Rectangle> workAreas, Point cursor)
    {
        if (workAreas.Count == 0) return Rectangle.Empty;
        var candidates = workAreas
            .Select(area => new { Area = area, Intersection = Rectangle.Intersect(area, frame) })
            .OrderByDescending(item => item.Intersection.Width * item.Intersection.Height)
            .ThenByDescending(item => item.Area.Contains(cursor))
            .ToArray();
        if (candidates[0].Intersection.Width > 0 && candidates[0].Intersection.Height > 0)
            return candidates[0].Area;
        return workAreas.FirstOrDefault(area => area.Contains(cursor), workAreas[0]);
    }

    public static int HeaderHeightPixels(double scale) => Math.Max(1, (int)Math.Round(34 * Math.Max(1, scale)));

    public static QrRecognitionFrameLayout CalculateLayout(
        Rectangle frame,
        IReadOnlyList<Rectangle> workAreas,
        Point cursor,
        int headerHeightPixels)
    {
        var workArea = SelectTargetWorkArea(frame, workAreas, cursor);
        var window = new Rectangle(frame.Left, frame.Top - headerHeightPixels, frame.Width, frame.Height + headerHeightPixels);
        return new QrRecognitionFrameLayout(frame, window, workArea, headerHeightPixels);
    }

    // Kept for callers compiled against the earlier geometry helper. The single-window
    // layout ignores the old toolbar size and uses a zero-height title bar.
    public static QrRecognitionFrameLayout CalculateLayout(Rectangle frame, IReadOnlyList<Rectangle> workAreas, Point cursor, Size _) =>
        CalculateLayout(frame, workAreas, cursor, 0);

    public static Rectangle Resize(Rectangle original, int dx, int dy, ResizeHandle handle)
        => Resize(original, dx, dy, handle, MinimumWidthDip, MinimumHeightDip);

    public static Rectangle Resize(Rectangle original, int dx, int dy, ResizeHandle handle, int minimumWidth, int minimumHeight)
    {
        var left = original.Left;
        var top = original.Top;
        var right = original.Right;
        var bottom = original.Bottom;
        if (handle.HasFlag(ResizeHandle.Left)) left += dx;
        if (handle.HasFlag(ResizeHandle.Right)) right += dx;
        if (handle.HasFlag(ResizeHandle.Top)) top += dy;
        if (handle.HasFlag(ResizeHandle.Bottom)) bottom += dy;
        if (right - left < minimumWidth)
        {
            if (handle.HasFlag(ResizeHandle.Left)) left = right - minimumWidth; else right = left + minimumWidth;
        }
        if (bottom - top < minimumHeight)
        {
            if (handle.HasFlag(ResizeHandle.Top)) top = bottom - minimumHeight; else bottom = top + minimumHeight;
        }
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    public static Rectangle Move(Rectangle original, int dx, int dy) => new(
        original.Left + dx,
        original.Top + dy,
        original.Width,
        original.Height);

    public static Rectangle ToolBarBounds(Rectangle frame, Rectangle workArea, int width = 240, int height = 42)
    {
        var left = Math.Clamp(frame.Left + (frame.Width - width) / 2, workArea.Left, workArea.Right - width);
        var below = frame.Bottom + 8;
        var top = below + height <= workArea.Bottom ? below : frame.Top - height - 8;
        return new Rectangle(left, Math.Clamp(top, workArea.Top, workArea.Bottom - height), width, height);
    }
}

public sealed record QrRecognitionFrameLayout(
    Rectangle CaptureBounds,
    Rectangle WindowBounds,
    Rectangle TargetWorkArea,
    int HeaderHeightPixels)
{
    public Rectangle FrameBounds => CaptureBounds;
    public Rectangle ToolbarBounds => Rectangle.Empty;
    public bool ToolbarAbove => false;
}

[Flags]
public enum ResizeHandle
{
    None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8
}
