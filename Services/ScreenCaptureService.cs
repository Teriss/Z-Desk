using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FormsScreen = System.Windows.Forms.Screen;
using ZDesk.Models;

namespace ZDesk.Services;

public static class ScreenCaptureService
{
    public static QrCaptureFrame? CaptureRegion(Rectangle selection)
    {
        var displays = FormsScreen.AllScreens
            .Select(screen => screen.Bounds)
            .Where(bounds => bounds.Width > 0 && bounds.Height > 0)
            .ToArray();
        if (displays.Length == 0 || selection.Width <= 0 || selection.Height <= 0) return null;
        checked
        {
            var stride = selection.Width * 4;
            var pixels = new byte[stride * selection.Height];
            for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 255; pixels[i + 1] = 255; pixels[i + 2] = 255; pixels[i + 3] = 255; }
            foreach (var display in displays)
            {
                var intersection = Rectangle.Intersect(selection, display);
                if (intersection.IsEmpty) continue;
                var captured = Capture(intersection);
                var dstX = intersection.Left - selection.Left;
                var dstY = intersection.Top - selection.Top;
                for (var row = 0; row < intersection.Height; row++)
                    Buffer.BlockCopy(captured.Pixels, row * captured.Stride, pixels, (dstY + row) * stride + dstX * 4, intersection.Width * 4);
            }
            return new QrCaptureFrame(selection, pixels, stride);
        }
    }

    public static BitmapSource ToBitmapSource(QrCaptureFrame frame)
    {
        var source = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            frame.Pixels,
            frame.Stride);
        source.Freeze();
        return source;
    }

    private static QrCaptureFrame Capture(Rectangle bounds)
    {
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        var lockBounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(lockBounds, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var pixels = new byte[stride * bitmap.Height];
            if (data.Stride >= 0)
            {
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            }
            else
            {
                for (var row = 0; row < bitmap.Height; row++)
                {
                    var source = data.Scan0 + (bitmap.Height - 1 - row) * data.Stride;
                    Marshal.Copy(source, pixels, row * stride, stride);
                }
            }
            return new QrCaptureFrame(bounds, pixels, stride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
