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
        try
        {
            var engine = ResolveEngine(languages);
            if (engine is null)
            {
                _logger.LogDebug("No Windows.Media.Ocr language pack installed; OCR unavailable");
                return [];
            }

            // Stream -> InMemoryRandomAccessStream (UWP API requirement)
            using var raStream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(raStream.GetOutputStreamAt(0)))
            {
                using var ms = new MemoryStream();
                pngStream.CopyTo(ms);
                writer.WriteBytes(ms.ToArray());
                writer.StoreAsync().GetAwaiter().GetResult();
                writer.FlushAsync().GetAwaiter().GetResult();
                writer.DetachStream();
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
                // Union of all word polygons -> line bounding rect.
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
                    Confidence: 1.0));  // UWP API doesn't expose per-line confidence
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

    private static OcrEngine? ResolveEngine(IReadOnlyList<string>? languages)
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
                catch
                {
                    // Bad tag, try next
                }
            }
        }
        // Fallback: first OS-installed language pack with OCR support.
        return OcrEngine.TryCreateFromUserProfileLanguages();
    }
}
