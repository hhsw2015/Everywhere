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
    [Obsolete("Use ParseGrouped — Parse drops the per-annotation strokes the snapper needs.")]
    public static IReadOnlyList<Annotation> Parse(IReadOnlyList<Stroke> strokes)
    {
        var (anns, _) = ParseGrouped(strokes);
        return anns;
    }

    /// <summary>
    /// Parse strokes into annotations AND return the strokes that backed
    /// each annotation. The snapper needs the latter so that, e.g.,
    /// SnapArrow inspects only that arrow's stroke endpoints — not every
    /// endpoint in the session. Critical when a session has multiple
    /// gestures: passing the full list pulls in unrelated coordinates and
    /// lands snaps on whatever leaf happens to sit under an unrelated
    /// stroke endpoint.
    /// </summary>
    public static (IReadOnlyList<Annotation> Annotations,
                   IReadOnlyList<IReadOnlyList<Stroke>> StrokeGroups)
        ParseGrouped(IReadOnlyList<Stroke> strokes)
    {
        if (strokes is null || strokes.Count == 0) return ([], []);
        var groups = GroupStrokes(strokes);
        var annotations = new List<Annotation>(groups.Count);
        var groupViews = new List<IReadOnlyList<Stroke>>(groups.Count);
        foreach (var g in groups)
        {
            var kind = Classify(g);
            var rect = KindToRect(kind, g);
            annotations.Add(new Annotation(rect, kind));
            groupViews.Add(g);
        }
        return (annotations, groupViews);
    }

    // -------------------------------------------------------------------
    // Grouping (geometric: bbox overlap or close center distance)
    // -------------------------------------------------------------------

    /// <summary>
    /// Default to one-stroke-per-gesture. Two strokes only merge when
    /// they form a recognisable multi-stroke shape: an X (chords cross
    /// at near-right angles, similar lengths) or an arrow (axis + small
    /// head meeting near an endpoint). Anything else stays independent.
    ///
    /// Why: prior versions used geometric proximity + a time window. Both
    /// signals fail when the user draws several distinct gestures within
    /// 2 seconds and < 200 px apart — they merged unrelated shapes,
    /// poisoning the snapper with stroke endpoints from other gestures.
    /// Strict shape recognition is the only reliable boundary.
    /// </summary>
    private static List<List<Stroke>> GroupStrokes(IReadOnlyList<Stroke> strokes)
    {
        var groups = new List<List<Stroke>>();
        foreach (var s in strokes)
        {
            if (s.Points.Count == 0) continue;
            groups.Add([s]);
        }
        // Try to merge each pair into an Arrow or X gesture. Arrow is
        // checked FIRST because X's angle-fallback (35-145°) is wide
        // enough to swallow shaft+barb arrows whose two strokes cross
        // near-perpendicularly at the tip. Arrow's matcher is stricter
        // (axis must dominate length, head endpoints must sit near one
        // axis endpoint) so a real X never matches it.
        for (var i = 0; i < groups.Count; i++)
        {
            for (var j = i + 1; j < groups.Count; j++)
            {
                if (groups[i].Count != 1 || groups[j].Count != 1) continue;
                var pair = new List<Stroke> { groups[i][0], groups[j][0] };
                if (LooksLikeArrow(pair) || LooksLikeX(pair))
                {
                    groups[i].Add(groups[j][0]);
                    groups.RemoveAt(j);
                    break;
                }
            }
        }
        return groups;
    }

    // -------------------------------------------------------------------
    // Classification
    // -------------------------------------------------------------------

    private static AnnotationKind Classify(List<Stroke> strokes)
    {
        if (strokes.Count == 2)
        {
            // GroupStrokes only ever merges pairs that already pass
            // LooksLikeArrow or LooksLikeX. Re-check, Arrow first —
            // X's angle fallback is permissive enough to also match
            // shaft+barb arrows, so giving X priority here would
            // silently re-classify every two-stroke arrow as X.
            if (LooksLikeArrow(strokes)) return AnnotationKind.Arrow;
            if (LooksLikeX(strokes)) return AnnotationKind.X;
            return AnnotationKind.Circle; // shouldn't happen with the new grouper
        }
        var s = strokes[0];
        var bb = StrokeBBox(s);
        // Reject only truly degenerate strokes (single tap, no extent).
        // The previous test bb.Width * bb.Height < 1 rejected perfect
        // horizontal underlines (0 vertical jitter from a fast user)
        // because Width * 0 = 0. Use the longer side instead — a real
        // gesture extends in at least one axis.
        if (Math.Max(bb.Width, bb.Height) < 5.0) return AnnotationKind.Unknown;

        var straight = Straightness(s);
        // Closed-loop: start ≈ end and curvy → Circle even if not
        // particularly twisty.
        var pts = s.Points;
        var dx = pts[0].X - pts[^1].X;
        var dy = pts[0].Y - pts[^1].Y;
        var pathLen = PathLength(s);
        var closure = pathLen > 0 ? Math.Sqrt(dx * dx + dy * dy) / pathLen : 1.0;
        if (closure < 0.2 && straight < 0.5) return AnnotationKind.Circle;
        if (straight < 0.3) return AnnotationKind.Circle;
        // Straight stroke without closing back on itself = underline.
        // 0.75 was the historical cutoff; we fall through to it after
        // the closure check so a barely-curved underline still classifies
        // correctly (drift to Arrow only happens when neither closed nor
        // straight enough — i.e. the user actually drew a curved arc).
        if (straight > 0.75) return AnnotationKind.Underline;
        return AnnotationKind.Arrow;
    }

    private static bool LooksLikeX(List<Stroke> strokes)
    {
        for (var i = 0; i < strokes.Count; i++)
        for (var j = i + 1; j < strokes.Count; j++)
        {
            var a = strokes[i].Points;
            var b = strokes[j].Points;
            if (a.Count < 2 || b.Count < 2) continue;
            if (!SegmentsCross(a[0], a[^1], b[0], b[^1])) continue;
            var lenA = Math.Sqrt(Sq(a[^1].X - a[0].X) + Sq(a[^1].Y - a[0].Y));
            var lenB = Math.Sqrt(Sq(b[^1].X - b[0].X) + Sq(b[^1].Y - b[0].Y));
            if (lenA < 5 || lenB < 5) continue;
            var ratio = lenA / lenB;
            // Mid-point coincidence: a real X has both chords passing
            // through a common centre — their midpoints sit close
            // together. Catches wide-flat X (chords near-horizontal,
            // angle as low as 8°) that the angle gate below misses.
            // No length-ratio gate here: a wide-flat X drawn over one
            // line of text has an extreme ratio (200px wide × 25px
            // tall → ratio 8) yet midpoints still coincide.
            var midAX = (a[0].X + a[^1].X) * 0.5;
            var midAY = (a[0].Y + a[^1].Y) * 0.5;
            var midBX = (b[0].X + b[^1].X) * 0.5;
            var midBY = (b[0].Y + b[^1].Y) * 0.5;
            var midDist = Math.Sqrt(Sq(midAX - midBX) + Sq(midAY - midBY));
            var avgLen = (lenA + lenB) * 0.5;
            if (avgLen > 0 && midDist / avgLen <= 0.3) return true;
            // Angle-fallback: two strokes meeting near-perpendicular at
            // ANY point (not necessarily the centre). On its own this
            // gate also accepts shaft+barb-V arrows whose strokes cross
            // at the tip, so guard with a strict length-parity band —
            // a real X has similar-length chords (ratio in 0.4..2.5);
            // an axis+barb arrow has ratio 2.5+ and is rejected here.
            if (ratio < 0.4 || ratio > 2.5) continue;
            var angle = AngleBetween(a[0], a[^1], b[0], b[^1]);
            if (angle >= 35 && angle <= 145) return true;
        }
        return false;
    }

    private static bool LooksLikeArrow(List<Stroke> strokes)
    {
        // Axis + head: longer stroke is the axis, shorter is the head;
        // they meet near one of the axis's endpoints with a small angle
        // (the arrowhead pinches in).
        if (strokes.Count < 2) return false;
        var a = strokes[0].Points;
        var b = strokes[1].Points;
        if (a.Count < 2 || b.Count < 2) return false;
        var lenA = Math.Sqrt(Sq(a[^1].X - a[0].X) + Sq(a[^1].Y - a[0].Y));
        var lenB = Math.Sqrt(Sq(b[^1].X - b[0].X) + Sq(b[^1].Y - b[0].Y));
        if (lenA < 5 || lenB < 5) return false;
        var axisLen = Math.Max(lenA, lenB);
        var headLen = Math.Min(lenA, lenB);
        var (axis, head) = lenA >= lenB ? (a, b) : (b, a);
        // Head ≤ ~95% of axis. Users sometimes draw the head V as long
        // as the shaft (a "hook" arrow). Pure length comparison can't
        // disambiguate Arrow from X; the endpoint-proximity check below
        // is the actual disambiguator. Real X has chord midpoints that
        // coincide (handled by LooksLikeX) and chord endpoints that
        // DON'T sit near each other — fails Arrow's NearEither test.
        if (headLen / axisLen > 0.95) return false;
        // Endpoints within 30% of axis length. Originally 25%; widened
        // because arrow heads drawn as one V often have chord endpoints
        // a bit further from the axis tip than 25% allows when the V
        // is drawn open.
        var thr = Math.Max(15, axisLen * 0.30);
        bool NearEither(StrokePoint p) =>
            Math.Sqrt(Sq(p.X - axis[0].X) + Sq(p.Y - axis[0].Y)) < thr ||
            Math.Sqrt(Sq(p.X - axis[^1].X) + Sq(p.Y - axis[^1].Y)) < thr;
        return NearEither(head[0]) || NearEither(head[^1]);
    }

    private static double AngleBetween(StrokePoint a0, StrokePoint a1, StrokePoint b0, StrokePoint b1)
    {
        var ax = a1.X - a0.X; var ay = a1.Y - a0.Y;
        var bx = b1.X - b0.X; var by = b1.Y - b0.Y;
        var la = Math.Sqrt(ax * ax + ay * ay);
        var lb = Math.Sqrt(bx * bx + by * by);
        if (la == 0 || lb == 0) return 0;
        var cos = (ax * bx + ay * by) / (la * lb);
        cos = Math.Clamp(cos, -1.0, 1.0);
        return Math.Acos(cos) * 180.0 / Math.PI;
    }

    private static double Sq(double v) => v * v;

    private static double PathLength(Stroke s)
    {
        var total = 0.0;
        for (var i = 1; i < s.Points.Count; i++)
        {
            var dx = s.Points[i].X - s.Points[i - 1].X;
            var dy = s.Points[i].Y - s.Points[i - 1].Y;
            total += Math.Sqrt(dx * dx + dy * dy);
        }
        return total;
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
