using Avalonia.Media.Imaging;
using Everywhere.Interop;
using SkiaSharp;

namespace Everywhere.Mcp.Snapshot;

public enum ScreenshotFormat
{
    /// <summary>JPEG, 3-5× smaller than PNG; lossy. Default for agent context.</summary>
    Jpeg,
    /// <summary>PNG, lossless. Use only when bit-perfect (OCR / diff) matters.</summary>
    Png,
}

public sealed record ScreenshotEncodeOptions(
    ScreenshotFormat Format = ScreenshotFormat.Jpeg,
    int Quality = 80,
    int MaxHeight = 1080);

/// <summary>
/// Encodes a captured bitmap into a base64 string. Default JPEG@80 / 1080p height
/// keeps a typical 1728×1080 desktop screenshot at ~150-300 KB ⇒ ~50-75 K agent
/// tokens, vs. PNG-100 ~1 MB ⇒ ~330 K tokens.
/// </summary>
public static class ScreenshotEncoder
{
    public static string? EncodeBase64(
        IVisualElement.ICapturedBitmapData captured,
        ScreenshotEncodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(captured);
        var opts = options ?? new();

        var skImage = captured.ToSKImage();
        if (skImage is null) return null;

        try
        {
            return Encode(skImage, opts);
        }
        finally
        {
            skImage.Dispose();
        }
    }

    public static string? EncodeBase64(Bitmap? bitmap, ScreenshotEncodeOptions? options = null)
    {
        if (bitmap is null) return null;
        var opts = options ?? new();

        // Avalonia.Bitmap doesn't expose a direct encoding format selector; round-trip
        // through Skia so we can pick JPEG.
        using var ms = new MemoryStream();
        bitmap.Save(ms);
        ms.Position = 0;
        using var skImage = SKImage.FromEncodedData(ms);
        return skImage is null ? null : Encode(skImage, opts);
    }

    // Back-compat: keep the old PNG-only API alive for any callers we missed.
    public static string? EncodePngBase64(IVisualElement.ICapturedBitmapData captured) =>
        EncodeBase64(captured, new(ScreenshotFormat.Png, 100, 0));

    public static string? EncodePngBase64(Bitmap? bitmap) =>
        EncodeBase64(bitmap, new(ScreenshotFormat.Png, 100, 0));

    private static string Encode(SKImage source, ScreenshotEncodeOptions opts)
    {
        var (w, h) = ComputeSize(source.Width, source.Height, opts.MaxHeight);
        var quality = Math.Clamp(opts.Quality, 1, 100);

        byte[] bytes;
        if (w == source.Width && h == source.Height)
        {
            using var data = source.Encode(SkiaFormat(opts.Format), quality);
            bytes = data.ToArray();
        }
        else
        {
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface is null)
            {
                using var data = source.Encode(SkiaFormat(opts.Format), quality);
                bytes = data.ToArray();
            }
            else
            {
                using var paint = new SKPaint { IsAntialias = true };
                var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
                surface.Canvas.DrawImage(source, new SKRect(0, 0, w, h), sampling, paint);
                using var snapshot = surface.Snapshot();
                using var encoded = snapshot.Encode(SkiaFormat(opts.Format), quality);
                bytes = encoded.ToArray();
            }
        }
        return Convert.ToBase64String(bytes);
    }

    private static (int W, int H) ComputeSize(int srcW, int srcH, int maxHeight)
    {
        if (maxHeight <= 0 || srcH <= maxHeight) return (srcW, srcH);
        var ratio = (double)maxHeight / srcH;
        var w = Math.Max(2, (int)Math.Round(srcW * ratio));
        var h = Math.Max(2, maxHeight);
        // Some encoders (yuv420p downstream) require even dimensions.
        if (w % 2 != 0) w--;
        if (h % 2 != 0) h--;
        return (w, h);
    }

    private static SKEncodedImageFormat SkiaFormat(ScreenshotFormat f) => f switch
    {
        ScreenshotFormat.Png => SKEncodedImageFormat.Png,
        _ => SKEncodedImageFormat.Jpeg,
    };
}
