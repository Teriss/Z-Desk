using System.Drawing;
using ZXingCpp;
using ZDesk.Models;

namespace ZDesk.Services;

public static class QrCodeRecognitionService
{
    private static readonly Binarizer[] Binarizers = [Binarizer.LocalAverage, Binarizer.GlobalHistogram];

    public static IReadOnlyList<QrCodeRecognitionResult> Decode(QrCaptureFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Pixels.Length < frame.Stride * frame.Height)
            return [];

        var results = new List<QrCodeRecognitionResult>();
        DecodeCandidate(frame.Pixels, frame.Width, frame.Height, ImageFormat.BGRA, frame.Stride, 4, results);

        foreach (var channel in new[] { -1, 0, 1, 2 })
        {
            var candidate = CreateContrastCandidate(frame, channel);
            DecodeCandidate(candidate, frame.Width, frame.Height, ImageFormat.Lum, frame.Width, 1, results);
        }

        return results
            .OrderBy(result => result.Y)
            .ThenBy(result => result.X)
            .ToArray();
    }

    private static void DecodeCandidate(
        byte[] pixels,
        int width,
        int height,
        ImageFormat format,
        int rowStride,
        int pixelStride,
        List<QrCodeRecognitionResult> results)
    {
        foreach (var binarizer in Binarizers)
        {
            using var reader = new BarcodeReader
            {
                Formats = BarcodeFormat.QRCode,
                TryHarder = true,
                TryRotate = true,
                TryInvert = true,
                TryDownscale = true,
                MaxNumberOfSymbols = 64,
                Binarizer = binarizer
            };
            var image = new ImageView(pixels, width, height, format, rowStride, pixelStride);
            foreach (var barcode in reader.From(image))
            {
                using (barcode)
                {
                    if (!barcode.IsValid) continue;
                    var result = new QrCodeRecognitionResult(barcode.Text ?? string.Empty, GetBounds(barcode.Position));
                    if (results.Any(existing => IsSamePhysicalCode(existing, result))) continue;
                    results.Add(result);
                }
            }
        }
    }

    private static byte[] CreateContrastCandidate(QrCaptureFrame frame, int channel)
    {
        var candidate = new byte[frame.Width * frame.Height];
        var histogram = new int[256];
        for (var y = 0; y < frame.Height; y++)
        {
            var sourceOffset = y * frame.Stride;
            var destinationOffset = y * frame.Width;
            for (var x = 0; x < frame.Width; x++)
            {
                var source = sourceOffset + x * 4;
                var value = channel switch
                {
                    0 => frame.Pixels[source],
                    1 => frame.Pixels[source + 1],
                    2 => frame.Pixels[source + 2],
                    _ => (byte)((frame.Pixels[source] * 29 + frame.Pixels[source + 1] * 150 + frame.Pixels[source + 2] * 77) >> 8)
                };
                candidate[destinationOffset + x] = value;
                histogram[value]++;
            }
        }

        var lower = Percentile(histogram, candidate.Length, 2);
        var upper = Percentile(histogram, candidate.Length, 98);
        if (upper <= lower) return candidate;

        var scale = 255d / (upper - lower);
        for (var index = 0; index < candidate.Length; index++)
        {
            candidate[index] = (byte)Math.Clamp((int)Math.Round((candidate[index] - lower) * scale), 0, 255);
        }
        return candidate;
    }

    private static int Percentile(IReadOnlyList<int> histogram, int total, int percentile)
    {
        var target = Math.Max(1, (int)Math.Ceiling(total * (percentile / 100d)));
        var accumulated = 0;
        for (var value = 0; value < histogram.Count; value++)
        {
            accumulated += histogram[value];
            if (accumulated >= target) return value;
        }
        return histogram.Count - 1;
    }

    private static Rectangle GetBounds(Position position)
    {
        var points = new[] { position.TopLeft, position.TopRight, position.BottomRight, position.BottomLeft };
        var left = points.Min(point => point.X);
        var top = points.Min(point => point.Y);
        var right = points.Max(point => point.X);
        var bottom = points.Max(point => point.Y);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static bool IsSamePhysicalCode(QrCodeRecognitionResult first, QrCodeRecognitionResult second)
    {
        if (!string.Equals(first.Text, second.Text, StringComparison.Ordinal)) return false;
        var intersection = Rectangle.Intersect(first.Bounds, second.Bounds);
        if (intersection.IsEmpty) return false;
        var firstArea = (long)first.Bounds.Width * first.Bounds.Height;
        var secondArea = (long)second.Bounds.Width * second.Bounds.Height;
        var minimumArea = Math.Min(firstArea, secondArea);
        var intersectionArea = (long)intersection.Width * intersection.Height;
        return minimumArea > 0 && intersectionArea * 100 >= minimumArea * 65;
    }
}
