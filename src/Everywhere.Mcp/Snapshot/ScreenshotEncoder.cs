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
    int Quality = 70,
    int MaxHeight = 1080,
    int MaxWidth = 1920);

/// <summary>
/// Encodes a captured bitmap into a base64 string. Default JPEG@70 /
/// 1920×1080 cap keeps a 5K-display window screenshot at ~70-100 KB ⇒
/// ~25-35 K agent tokens, vs. PNG-100 ~3 MB ⇒ ~1 M tokens. quality=70
/// is visually indistinguishable from 80 on UI chrome but ~15% smaller.
/// Pass MaxWidth=0 / MaxHeight=0 to disable that axis.
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
        EncodeBase64(bitmap, new(ScreenshotFormat.Png, 100, 0, 0));

    private static string Encode(SKImage source, ScreenshotEncodeOptions opts)
    {
        var (w, h) = ComputeSize(source.Width, source.Height, opts.MaxWidth, opts.MaxHeight);
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

    private static (int W, int H) ComputeSize(int srcW, int srcH, int maxWidth, int maxHeight)
    {
        // Pick the more restrictive axis so neither dimension exceeds its cap.
        var ratioW = maxWidth > 0 && srcW > maxWidth ? (double)maxWidth / srcW : 1.0;
        var ratioH = maxHeight > 0 && srcH > maxHeight ? (double)maxHeight / srcH : 1.0;
        var ratio = Math.Min(ratioW, ratioH);
        if (ratio >= 1.0) return (srcW, srcH);

        var w = Math.Max(2, (int)Math.Round(srcW * ratio));
        var h = Math.Max(2, (int)Math.Round(srcH * ratio));
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
