using Avalonia;

namespace Everywhere.Interop.Whiteboard;

/// <summary>
/// Parser output: a single gesture with classified intent and a geometric
/// rectangle. Snapper consumes this and returns a snapped rect bound to
/// real a11y leaves.
/// </summary>
public sealed record Annotation(
    Rect BoundingRect,
    AnnotationKind Kind,
    double Confidence = 1.0);
