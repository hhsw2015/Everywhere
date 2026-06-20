using Avalonia;
using Everywhere.Interop.Whiteboard;

namespace Everywhere.Mcp.Whiteboard;

/// <summary>
/// Pure geometric parser: stroke list -> annotation list.
///
/// Mirror of whiteboard-sandbox/src/parser.py; same algorithm, identical
/// outputs (verified by side-by-side fixture replay during development).
///
/// Pipeline:
///   1. Group strokes by time + space proximity (one gesture may have N strokes)
///   2. Classify each group by straightness:
///        ~0.0 = circle (path returns near start)
///        ~0.5 = arrow  (shaft + arrowhead doubles back)
///        ~0.9 = underline (nearly straight)
///        2+ strokes = X (only multi-stroke kind we generate)
///   3. Compute output Rect from stroke geometry per kind.
/// </summary>
public static class WhiteboardParser
{
    private const double TimeGapMs = 600;
    private const double SpaceDist = 300;

    public static IReadOnlyList<Annotation> Parse(IReadOnlyList<Stroke> strokes)
    {
        if (strokes is null || strokes.Count == 0) return [];
        var groups = GroupStrokes(strokes);
        var annotations = new List<Annotation>(groups.Count);
        foreach (var g in groups)
        {
            var kind = Classify(g);
            var rect = KindToRect(kind, g);
            annotations.Add(new Annotation(rect, kind));
        }
        return annotations;
    }

    // -------------------------------------------------------------------
    // Grouping
    // -------------------------------------------------------------------

    private static List<List<Stroke>> GroupStrokes(IReadOnlyList<Stroke> strokes)
    {
        var groups = new List<List<Stroke>>();
        if (strokes.Count == 0) return groups;
        groups.Add([strokes[0]]);
        for (var i = 1; i < strokes.Count; i++)
        {
            var s = strokes[i];
            var prevGroup = groups[^1];
            var prev = prevGroup[^1];
            var gap = s.TStart - prev.TEnd;
            var c1 = StrokeBBox(prev);
            var c2 = StrokeBBox(s);
            var dx = c1.Center.X - c2.Center.X;
            var dy = c1.Center.Y - c2.Center.Y;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (gap < TimeGapMs && dist < SpaceDist)
                prevGroup.Add(s);
            else
                groups.Add([s]);
        }
        return groups;
    }

    // -------------------------------------------------------------------
    // Classification
    // -------------------------------------------------------------------

    private static AnnotationKind Classify(List<Stroke> strokes)
    {
        if (strokes.Count >= 2) return AnnotationKind.X;
        var s = strokes[0];
        var bb = StrokeBBox(s);
        if (bb.Width * bb.Height < 1.0) return AnnotationKind.Unknown;

        var straight = Straightness(s);
        if (straight < 0.3) return AnnotationKind.Circle;
        if (straight > 0.75) return AnnotationKind.Underline;
        return AnnotationKind.Arrow;
    }

    private static double Straightness(Stroke s)
    {
        var len = StrokeLength(s);
        if (len < 1.0) return 0.0;
        var p0 = s.Points[0];
        var p1 = s.Points[^1];
        var dx = p0.X - p1.X;
        var dy = p0.Y - p1.Y;
        var chord = Math.Sqrt(dx * dx + dy * dy);
        return chord / len;
    }

    private static double StrokeLength(Stroke s)
    {
        var sum = 0.0;
        for (var i = 1; i < s.Points.Count; i++)
        {
            var dx = s.Points[i].X - s.Points[i - 1].X;
            var dy = s.Points[i].Y - s.Points[i - 1].Y;
            sum += Math.Sqrt(dx * dx + dy * dy);
        }
        return sum;
    }

    // -------------------------------------------------------------------
    // Per-kind rect
    // -------------------------------------------------------------------

    private static Rect KindToRect(AnnotationKind kind, List<Stroke> strokes)
    {
        var bb = MultiStrokeBBox(strokes);

        return kind switch
        {
            AnnotationKind.Circle => bb.Inflate(-2),
            AnnotationKind.Underline => UnderlineRect(strokes, bb),
            AnnotationKind.Arrow => ArrowRect(strokes),
            AnnotationKind.X => bb,
            _ => bb,
        };
    }

    private static Rect UnderlineRect(List<Stroke> strokes, Rect bb)
    {
        // Baseline = stroke median y (robust to noise, handles tilt).
        // The snap stage refines this to the real text element bbox.
        var ys = new List<double>();
        foreach (var s in strokes)
        foreach (var p in s.Points)
            ys.Add(p.Y);
        ys.Sort();
        var baseline = ys[ys.Count / 2];
        const double lineH = 28.0;
        return new Rect(bb.X, baseline - lineH, bb.Width, lineH);
    }

    private static Rect ArrowRect(List<Stroke> strokes)
    {
        // Arrow tip = stroke-set point farthest from the start. Robust to
        // shaft + arrowhead being a single stroke that doubles back.
        var start = strokes[0].Points[0];
        var bestX = start.X; var bestY = start.Y; var bestD2 = -1.0;
        foreach (var s in strokes)
        foreach (var p in s.Points)
        {
            var dx = p.X - start.X;
            var dy = p.Y - start.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 > bestD2) { bestD2 = d2; bestX = p.X; bestY = p.Y; }
        }
        return new Rect(bestX - 100, bestY - 50, 200, 100);
    }

    // -------------------------------------------------------------------
    // bbox helpers
    // -------------------------------------------------------------------

    internal static Rect StrokeBBox(Stroke s)
    {
        if (s.Points.Count == 0) return new Rect(0, 0, 0, 0);
        double minX = s.Points[0].X, maxX = s.Points[0].X;
        double minY = s.Points[0].Y, maxY = s.Points[0].Y;
        for (var i = 1; i < s.Points.Count; i++)
        {
            var p = s.Points[i];
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static Rect MultiStrokeBBox(List<Stroke> strokes)
    {
        var minX = double.PositiveInfinity; var maxX = double.NegativeInfinity;
        var minY = double.PositiveInfinity; var maxY = double.NegativeInfinity;
        foreach (var s in strokes)
        foreach (var p in s.Points)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }
        if (double.IsPositiveInfinity(minX)) return new Rect(0, 0, 0, 0);
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }
}
