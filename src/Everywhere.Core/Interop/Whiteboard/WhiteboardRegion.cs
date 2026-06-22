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
    IReadOnlyList<OcrLine> OcrLines,
    /// <summary>
    /// Image leaves (AXImage / VisualElementType.Image) inside this
    /// gesture, separate from text leaves. The tool returns metadata
    /// (alt, bbox, image_id) for these by default — callers fetch the
    /// actual pixels via read_whiteboard_image(image_id) on demand, so
    /// images don't pay the multimodal token cost unless the agent or
    /// user explicitly asks for them.
    /// </summary>
    IReadOnlyList<WhiteboardImageLeaf> ImageLeaves);

/// <summary>
/// One image inside a whiteboard region. Carries enough metadata for the
/// agent to decide whether to call read_whiteboard_image(image_id) and
/// pull the pixels into context.
/// </summary>
public sealed record WhiteboardImageLeaf(
    string ImageId,
    string Alt,
    PixelRect Bbox,
    /// <summary>PNG bytes of the cropped image, ready to base64 when the
    /// caller requests it. May be null if cropping failed; the agent then
    /// only sees the metadata.</summary>
    byte[]? PngBytes);
