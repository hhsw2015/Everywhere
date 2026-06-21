using System.IO;
using Avalonia;
using Everywhere.Interop.Whiteboard;
using Microsoft.Extensions.Logging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Everywhere.Windows.Interop;

/// <summary>
/// Windows.Media.Ocr (UWP API)-backed OCR. The engine ships with the OS
/// (Windows 10 1809+); the language packs are user-installed via Settings
/// → Time & Language → Language. We pick the first available language
/// from the requested list, falling back to whatever
/// <see cref="OcrEngine.AvailableRecognizerLanguages"/> reports.
///
/// Bbox accuracy matches Apple Vision (per-word polygons, we collapse
/// to per-line by grouping words at the same vertical band). Text is
/// authoritative-from-a11y in the hybrid path so OCR text quality is
/// not critical here.
/// </summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    private readonly ILogger<WindowsOcrEngine> _logger;

    public WindowsOcrEngine(ILogger<WindowsOcrEngine> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<OcrLine> Recognize(
        Stream pngStream,
        PixelPoint originPx,
        OcrQuality quality = OcrQuality.Fast,
        IReadOnlyList<string>? languages = null)
    {
        if (pngStream is null) return [];
        // Windows.Media.Ocr offers no quality knob — note the limitation
        // so callers can decide on a different engine for image-only PDFs.
        if (quality == OcrQuality.Accurate)
        {
            _logger.LogDebug(
                "Windows.Media.Ocr does not expose Accurate vs Fast levels; treating as Fast.");
        }
        // Run on a thread-pool thread to avoid sync-over-async deadlocks
        // when called from a UI/dispatcher SynchronizationContext.
        return Task.Run(() => RecognizeCore(pngStream, originPx, languages))
            .GetAwaiter().GetResult();
    }

    private IReadOnlyList<OcrLine> RecognizeCore(
        Stream pngStream, PixelPoint originPx, IReadOnlyList<string>? languages)
    {
        try
        {
            var engine = ResolveEngine(languages);
            if (engine is null)
            {
                _logger.LogDebug("No Windows.Media.Ocr language pack installed; OCR unavailable");
                return [];
            }

            // Stream -> IRandomAccessStream. AsStreamForWrite gives us a
            // managed Stream wrapper that writes directly into raStream,
            // skipping the DataWriter detour and the extra MemoryStream
            // copy.
            using var raStream = new InMemoryRandomAccessStream();
            using (var outStream = raStream.AsStreamForWrite())
            {
                if (pngStream.CanSeek) pngStream.Position = 0;
                pngStream.CopyTo(outStream);
            }
            raStream.Seek(0);

            var decoder = BitmapDecoder.CreateAsync(raStream).GetAwaiter().GetResult();
            using var softwareBitmap = decoder.GetSoftwareBitmapAsync().GetAwaiter().GetResult();

            var result = engine.RecognizeAsync(softwareBitmap).GetAwaiter().GetResult();
            if (result is null || result.Lines is null || result.Lines.Count == 0) return [];

            var lines = new List<OcrLine>(result.Lines.Count);
            foreach (var line in result.Lines)
            {
                if (line.Words is null || line.Words.Count == 0) continue;
                double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
                double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
                foreach (var w in line.Words)
                {
                    var b = w.BoundingRect;
                    if (b.X < minX) minX = b.X;
                    if (b.Y < minY) minY = b.Y;
                    if (b.X + b.Width > maxX) maxX = b.X + b.Width;
                    if (b.Y + b.Height > maxY) maxY = b.Y + b.Height;
                }
                if (double.IsInfinity(minX)) continue;
                var x = (int)Math.Round(minX) + originPx.X;
                var y = (int)Math.Round(minY) + originPx.Y;
                var w2 = Math.Max(1, (int)Math.Round(maxX - minX));
                var h2 = Math.Max(1, (int)Math.Round(maxY - minY));
                lines.Add(new OcrLine(
                    Text: line.Text ?? string.Empty,
                    Bounds: new PixelRect(x, y, w2, h2),
                    // Windows.Media.Ocr does not expose per-line confidence.
                    // Use NaN so consumers can detect "unknown" rather than
                    // mistaking it for high confidence.
                    Confidence: double.NaN));
            }
            lines.Sort((a, b) => a.Bounds.Y.CompareTo(b.Bounds.Y));
            return lines;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Windows OCR failed");
            return [];
        }
    }

    private OcrEngine? ResolveEngine(IReadOnlyList<string>? languages)
    {
        if (languages is { Count: > 0 })
        {
            foreach (var tag in languages)
            {
                try
                {
                    var lang = new Language(tag);
                    if (OcrEngine.IsLanguageSupported(lang))
                        return OcrEngine.TryCreateFromLanguage(lang);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "Windows OCR: invalid language tag {Tag}, skipping", tag);
                }
            }
        }
        // Fallback: first OS-installed language pack with OCR support.
        return OcrEngine.TryCreateFromUserProfileLanguages();
    }
}
