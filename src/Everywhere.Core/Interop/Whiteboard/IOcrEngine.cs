using Avalonia;

namespace Everywhere.Interop.Whiteboard;

/// <summary>
/// Per-line OCR result. <see cref="Bounds"/> is in screen pixels matching
/// <see cref="IVisualElement.BoundingRectangle"/>'s coord space (caller
/// passes <c>originPx</c> to <see cref="IOcrEngine.Recognize"/> so the
/// engine can pre-translate).
/// </summary>
public sealed record OcrLine(string Text, PixelRect Bounds, double Confidence);

/// <summary>
/// Recognition quality / latency trade-off.
///   Fast      — ~30 ms typical block, characters are garbage on CJK,
///                bbox-y is identical to Accurate. Use when only the
///                geometry matters (whiteboard slice).
///   Accurate  — ~250 ms typical block, full-quality text. Use when the
///                OCR text itself is consumed (image-only PDFs etc).
/// </summary>
public enum OcrQuality
{
    Fast,
    Accurate,
}

/// <summary>
/// Platform-abstracted OCR. Implementations: Mac uses Vision; Windows
/// would use Windows.Media.Ocr; Linux would use tesseract.
///
/// Designed to be shareable across Everywhere features: whiteboard slice,
/// file-extract-text-ocr strategies, future read_pick fallbacks. Register
/// a single instance per app via DI and inject where needed.
/// </summary>
public interface IOcrEngine
{
    /// <summary>
    /// Run OCR on a bitmap. Returns lines top-to-bottom in
    /// <paramref name="originPx"/> + image pixel space.
    /// Returns empty list on failure — never throws.
    /// </summary>
    IReadOnlyList<OcrLine> Recognize(
        System.IO.Stream pngStream,
        PixelPoint originPx,
        OcrQuality quality = OcrQuality.Fast,
        IReadOnlyList<string>? languages = null);
}

/// <summary>
/// No-op OCR engine for platforms / configurations where native OCR is
/// not available. Whiteboard slicer falls back to leaf-bbox proportional
/// slicing automatically when this is the registered implementation.
/// </summary>
public sealed class NullOcrEngine : IOcrEngine
{
    public IReadOnlyList<OcrLine> Recognize(
        System.IO.Stream pngStream, PixelPoint originPx,
        OcrQuality quality = OcrQuality.Fast,
        IReadOnlyList<string>? languages = null) => [];
}
