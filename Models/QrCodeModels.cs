using System.Drawing;

namespace ZDesk.Models;

public sealed record QrCaptureFrame(Rectangle Bounds, byte[] Pixels, int Stride)
{
    public int Width => Bounds.Width;
    public int Height => Bounds.Height;
}

public sealed record QrDesktopCapture(QrCaptureFrame Frame, IReadOnlyList<Rectangle> DisplayBounds);

public sealed record QrCodeRecognitionResult(string Text, Rectangle Bounds)
{
    public int X => Bounds.Left;
    public int Y => Bounds.Top;
}
