using Avalonia;

namespace Everywhere.Interop.Whiteboard;

/// <summary>
/// One annotated region: gesture kind + the a11y leaves it captures.
/// Multiple regions can ship together when a user draws several gestures
/// in one whiteboard session (e.g. circle + arrow + X on three places).
/// </summary>
public sealed record WhiteboardRegion(
    AnnotationKind Kind,
    Rect Rect,
    IReadOnlyList<IVisualElement> Leaves,
    double Confidence,
    /// <summary>
    /// Per-line OCR detections covering this region's screenshot, in
    /// screen-pixel coords. Used by HybridSlicer to slice a giant Label's
    /// text by the user's gesture rect. Empty when the OCR engine is the
    /// no-op stub or capture failed.
    /// </summary>
    IReadOnlyList<OcrLine> OcrLines);
