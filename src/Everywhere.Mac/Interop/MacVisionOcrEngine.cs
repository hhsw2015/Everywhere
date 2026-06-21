using System.IO;
using AppKit;
using Avalonia;
using CoreGraphics;
using Everywhere.Interop.Whiteboard;
using Foundation;
using ImageIO;
using Microsoft.Extensions.Logging;
using Vision;

namespace Everywhere.Mac.Interop;

/// <summary>
/// Apple Vision-backed OCR. Uses the Fast recognition level — ~30 ms / call
/// at desktop resolution; we only need per-line y bboxes (the text layer
/// comes from a11y), so Fast's lower text quality is acceptable and the
/// bbox accuracy is identical to Accurate in measurements.
///
/// Sandbox-verified (whiteboard-sandbox/run_hybrid_full.py): 6 fixtures /
/// 18 cases, avg Jaccard 0.898, recall 0.972, ~30ms / typical block.
/// </summary>
public sealed class MacVisionOcrEngine : IOcrEngine
{
    private readonly ILogger<MacVisionOcrEngine> _logger;

    public MacVisionOcrEngine(ILogger<MacVisionOcrEngine> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<OcrLine> Recognize(
        Stream pngStream,
        PixelPoint originPx,
        OcrQuality quality = OcrQuality.Fast,
        IReadOnlyList<string>? languages = null)
    {
        // Contract per IOcrEngine.Recognize: never throw, return empty on failure.
        if (pngStream is null) return [];
        CGImage? cg = null;
        try
        {
            using var pool = new NSAutoreleasePool();

            using (var data = NSData.FromStream(pngStream))
            {
                if (data is null) return [];
                using var src = CGImageSource.FromData(data);
                if (src is null || src.ImageCount == 0) return [];
                cg = src.CreateImage(0, new CGImageOptions());
            }
            if (cg is null) return [];

            {
                var imgW = (int)cg.Width;
                var imgH = (int)cg.Height;

                using var handler = new VNImageRequestHandler(cg, new NSDictionary());
                using var req = new VNRecognizeTextRequest((rq, err) => { });
                req.RecognitionLevel = quality == OcrQuality.Accurate
                    ? VNRequestTextRecognitionLevel.Accurate
                    : VNRequestTextRecognitionLevel.Fast;
                req.UsesLanguageCorrection = quality == OcrQuality.Accurate;
                req.RecognitionLanguages = languages?.ToArray()
                    ?? ["zh-Hans", "zh-Hant", "en-US"];

                if (!handler.Perform([req], out var nsErr) || nsErr is not null)
                {
                    if (nsErr is not null)
                        _logger.LogDebug("Vision OCR error: {Err}", nsErr.LocalizedDescription);
                    return [];
                }

                var results = req.GetResults<VNRecognizedTextObservation>();
                if (results is null || results.Length == 0) return [];

                var lines = new List<OcrLine>(results.Length);
                foreach (var obs in results)
                {
                    var cands = obs.TopCandidates(1);
                    if (cands is null || cands.Length == 0) continue;
                    var text = cands[0].String ?? string.Empty;

                    // Vision normalized rect: origin lower-left, 0..1 axes.
                    var bb = obs.BoundingBox;
                    var nx = bb.X;
                    var ny = bb.Y;
                    var nw = bb.Width;
                    var nh = bb.Height;

                    // Convert to upper-left-origin pixel coords + translate
                    // by originPx so output is in screen-pixel space.
                    // Round (not truncate) and clamp to >=1 — sub-pixel
                    // truncation would zero-out thin glyphs, which the
                    // hybrid slicer's 'smaller > 0' guard then drops.
                    var x = (int)Math.Round(nx * imgW) + originPx.X;
                    var w = Math.Max(1, (int)Math.Round(nw * imgW));
                    var y = (int)Math.Round((1.0 - ny - nh) * imgH) + originPx.Y;
                    var h = Math.Max(1, (int)Math.Round(nh * imgH));

                    lines.Add(new OcrLine(
                        Text: text,
                        Bounds: new PixelRect(x, y, w, h),
                        Confidence: cands[0].Confidence));
                }

                lines.Sort((a, b) => a.Bounds.Y.CompareTo(b.Bounds.Y));
                return lines;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vision OCR failed");
            return [];
        }
        finally
        {
            cg?.Dispose();
        }
    }
}
