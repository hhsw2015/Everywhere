namespace Everywhere.Interop.Whiteboard;

/// <summary>
/// A point in a whiteboard stroke. X / Y are screen pixels (matching
/// <see cref="IVisualElement.BoundingRectangle"/> coordinates), TimestampMs
/// is monotonic — used by the parser to group strokes that belong to the
/// same gesture (rapid succession + spatial proximity).
/// </summary>
public readonly record struct StrokePoint(double X, double Y, double TimestampMs);

/// <summary>
/// A single pen stroke (one mouse-down to mouse-up). Multiple strokes can
/// belong to one gesture (e.g. an X is two crossing strokes drawn back-to-back).
/// Immutable — copy on append at capture time.
/// </summary>
public sealed record Stroke(IReadOnlyList<StrokePoint> Points)
{
    public double TStart => Points.Count > 0 ? Points[0].TimestampMs : 0;
    public double TEnd   => Points.Count > 0 ? Points[^1].TimestampMs : 0;
}
