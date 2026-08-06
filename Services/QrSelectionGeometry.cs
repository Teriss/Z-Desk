using System.Drawing;
using ZDesk.Models;

namespace ZDesk.Services;

internal static class QrSelectionGeometry
{
    public const int MinimumSelectionPixels = 8;

    public static Rectangle Normalize(Point first, Point second) => Rectangle.FromLTRB(
        Math.Min(first.X, second.X),
        Math.Min(first.Y, second.Y),
        Math.Max(first.X, second.X),
        Math.Max(first.Y, second.Y));

    public static bool IsValid(Rectangle selection) =>
        selection.Width >= MinimumSelectionPixels && selection.Height >= MinimumSelectionPixels;

    public static QrCaptureFrame ComposeSelection(QrCaptureFrame frame, Rectangle selection)
    {
        if (!IsValid(selection)) throw new ArgumentOutOfRangeException(nameof(selection));
        checked
        {
            var stride = selection.Width * 4;
            var pixels = new byte[stride * selection.Height];
            Array.Fill(pixels, (byte)255);

            var intersection = Rectangle.Intersect(selection, frame.Bounds);
            if (!intersection.IsEmpty)
            {
                var sourceX = intersection.Left - frame.Bounds.Left;
                var sourceY = intersection.Top - frame.Bounds.Top;
                var destinationX = intersection.Left - selection.Left;
                var destinationY = intersection.Top - selection.Top;
                var rowLength = intersection.Width * 4;
                for (var row = 0; row < intersection.Height; row++)
                {
                    Buffer.BlockCopy(
                        frame.Pixels,
                        (sourceY + row) * frame.Stride + sourceX * 4,
                        pixels,
                        (destinationY + row) * stride + destinationX * 4,
                        rowLength);
                }
            }

            return new QrCaptureFrame(selection, pixels, stride);
        }
    }
}
