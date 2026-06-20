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
    double Confidence);
