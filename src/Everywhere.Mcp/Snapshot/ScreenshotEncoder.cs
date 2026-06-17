using Avalonia.Media.Imaging;
using Everywhere.Interop;
using SkiaSharp;

namespace Everywhere.Mcp.Snapshot;

/// <summary>
/// Encodes a captured bitmap into a base64 PNG that satisfies upstream's
/// <c>maxDimension=1280, minScale=0.25, maxPNGBytes=900_000</c> envelope: progressively
/// rescales by 0.85× until the encoded payload fits or the floor is reached.
/// </summary>
public static class ScreenshotEncoder
{
    public static string? EncodePngBase64(IVisualElement.ICapturedBitmapData captured)
    {
        ArgumentNullException.ThrowIfNull(captured);

        var skImage = captured.ToSKImage();
        if (skImage is null)
        {
            return null;
        }

        try
        {
            var scale = ComputeInitialScale(skImage.Width, skImage.Height);
            byte[] encoded;
            while (true)
            {
                encoded = EncodeAtScale(skImage, scale);
                if (encoded.Length <= UpstreamConstants.ScreenshotResultMaxPngBytes
                    || scale <= UpstreamConstants.ScreenshotResultMinScale)
                {
                    break;
                }
                scale = Math.Max(UpstreamConstants.ScreenshotResultMinScale, scale * 0.85);
            }

            return Convert.ToBase64String(encoded);
        }
        finally
        {
            skImage.Dispose();
        }
    }

    public static string? EncodePngBase64(Bitmap? bitmap)
    {
        if (bitmap is null)
        {
            return null;
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }

    private static double ComputeInitialScale(int width, int height)
    {
        var longest = Math.Max(width, height);
        if (longest <= UpstreamConstants.ScreenshotResultMaxDimension)
        {
            return 1.0;
        }
        return Math.Max(
            UpstreamConstants.ScreenshotResultMinScale,
            (double)UpstreamConstants.ScreenshotResultMaxDimension / longest);
    }

    private static byte[] EncodeAtScale(SKImage source, double scale)
    {
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        if (width == source.Width && height == source.Height)
        {
            using var data = source.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface is null)
        {
            using var data = source.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        using var paint = new SKPaint { IsAntialias = true };
        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
        surface.Canvas.DrawImage(source, new SKRect(0, 0, width, height), sampling, paint);
        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
