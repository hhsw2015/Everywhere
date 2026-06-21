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
    // Grouping (geometric: bbox overlap or close center distance)
    // -------------------------------------------------------------------

    private static List<List<Stroke>> GroupStrokes(IReadOnlyList<Stroke> strokes)
    {
        // Geometric merge — no time window. Two strokes belong to the
        // same gesture if their bboxes intersect, OR their centers are
        // within the LARGER bbox's diagonal (capped). The X case always
        // satisfies bbox-intersect; circles drawn in two arcs satisfy
        // center-distance.
        //
        // Then iterate to a fixed point so a bridge stroke pulls two
        // existing groups together. Group bboxes are CACHED and updated
        // by Rect.Union on merge — recomputing per pair would be
        // O(G^3 × points).
        var bboxes = new List<Rect>();
        var members = new List<List<Stroke>>();
        foreach (var s in strokes)
        {
            if (s.Points.Count == 0) continue;
            var bb = StrokeBBox(s);
            var merged = false;
            for (var gi = 0; gi < members.Count; gi++)
            {
                if (CanMerge(bb, bboxes[gi]))
                {
                    members[gi].Add(s);
                    bboxes[gi] = bboxes[gi].Union(bb);
                    merged = true;
                    break;
                }
            }
            if (!merged) { members.Add([s]); bboxes.Add(bb); }
        }
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var i = 0; i < members.Count && !changed; i++)
            for (var j = i + 1; j < members.Count && !changed; j++)
            {
                if (CanMerge(bboxes[i], bboxes[j]))
                {
                    members[i].AddRange(members[j]);
                    bboxes[i] = bboxes[i].Union(bboxes[j]);
                    members.RemoveAt(j);
                    bboxes.RemoveAt(j);
                    changed = true;
                }
            }
        }
        return members;
    }

    private static bool CanMerge(Rect a, Rect b)
    {
        var inter = a.Intersect(b);
        if (inter.Width > 0 && inter.Height > 0) return true;
        var aDiag = Math.Sqrt(a.Width * a.Width + a.Height * a.Height);
        var bDiag = Math.Sqrt(b.Width * b.Width + b.Height * b.Height);
        // Use the LARGER diagonal so a small new stroke can still merge
        // into a big group, but cap at 100px — three independent gestures
        // drawn ~150px apart on the same screen MUST stay separate, even
        // when one is large. 100px tracks the visual "this stroke clearly
        // belongs with that one" threshold for hand-drawn gestures.
        var threshold = Math.Min(100.0, Math.Max(1.0, Math.Max(aDiag, bDiag)));
        var dx = a.Center.X - b.Center.X;
        var dy = a.Center.Y - b.Center.Y;
        return Math.Sqrt(dx * dx + dy * dy) < threshold;
    }

    // -------------------------------------------------------------------
    // Classification
    // -------------------------------------------------------------------

    private static AnnotationKind Classify(List<Stroke> strokes)
    {
        if (strokes.Count >= 2)
        {
            // Real X: two strokes that actually CROSS (line segments
            // intersect). Without this check, two near-parallel scribbles
            // that the grouper bridged would silently classify as X.
            return HasCrossingStrokes(strokes)
                ? AnnotationKind.X
                : AnnotationKind.Circle;
        }
        var s = strokes[0];
        var bb = StrokeBBox(s);
        if (bb.Width * bb.Height < 1.0) return AnnotationKind.Unknown;

        var straight = Straightness(s);
        if (straight < 0.3) return AnnotationKind.Circle;
        if (straight > 0.75) return AnnotationKind.Underline;
        return AnnotationKind.Arrow;
    }

    private static bool HasCrossingStrokes(List<Stroke> strokes)
    {
        // Approximate each stroke by its endpoint-to-endpoint segment;
        // sample a few midpoint pairs as well so curvy strokes aren't
        // miscalled as parallel. For X we just need ONE pair of strokes
        // whose chord lines cross.
        for (var i = 0; i < strokes.Count; i++)
        for (var j = i + 1; j < strokes.Count; j++)
        {
            var a = strokes[i].Points;
            var b = strokes[j].Points;
            if (a.Count < 2 || b.Count < 2) continue;
            if (SegmentsCross(a[0], a[^1], b[0], b[^1])) return true;
        }
        return false;
    }

    private static bool SegmentsCross(StrokePoint p1, StrokePoint p2, StrokePoint p3, StrokePoint p4)
    {
        static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;
        var d1 = Cross(p4.X - p3.X, p4.Y - p3.Y, p1.X - p3.X, p1.Y - p3.Y);
        var d2 = Cross(p4.X - p3.X, p4.Y - p3.Y, p2.X - p3.X, p2.Y - p3.Y);
        var d3 = Cross(p2.X - p1.X, p2.Y - p1.Y, p3.X - p1.X, p3.Y - p1.Y);
        var d4 = Cross(p2.X - p1.X, p2.Y - p1.Y, p4.X - p1.X, p4.Y - p1.Y);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
            && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
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
