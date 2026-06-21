using Avalonia;
using Everywhere.Interop;
using Everywhere.Interop.Whiteboard;

namespace Everywhere.Mcp.Whiteboard;

/// <summary>
/// Snap a parser-output <see cref="Annotation"/> against a real a11y tree.
///
/// Mirrors whiteboard-sandbox/src/snapper.py — same per-kind logic and
/// reject thresholds. Production semantics match <see cref="ReadPickTool"/>:
/// we collect Label + Hyperlink leaves whose bounds intersect the
/// annotation rect, NOT a single "best element". Text concatenation is
/// done downstream.
/// </summary>
public static class AnnotationSnapper
{
    private static readonly HashSet<VisualElementType> LeafTextRoles =
    [
        VisualElementType.Label,
        VisualElementType.Hyperlink,
    ];

    public static SnapResult Snap(
        Annotation ann,
        IVisualElement root,
        IReadOnlyList<Stroke> strokes)
    {
        return ann.Kind switch
        {
            AnnotationKind.Arrow => SnapArrow(ann, root, strokes),
            AnnotationKind.Underline => SnapUnderline(ann, root, strokes),
            AnnotationKind.Circle => SnapCircleOrX(ann, root),
            AnnotationKind.X => SnapCircleOrX(ann, root),
            _ => Reject(ann.BoundingRect, "unknown gesture kind"),
        };
    }

    // -------------------------------------------------------------------
    // Arrow: tip-point lookup
    // -------------------------------------------------------------------

    private static SnapResult SnapArrow(
        Annotation ann,
        IVisualElement root,
        IReadOnlyList<Stroke> strokes)
    {
        // Direction-agnostic: the arrow tip is whichever stroke endpoint
        // sits closer to text. Users draw arrows in both directions
        // ("look at this!" from left or right), so we cannot assume
        // start = tail / end = tip.
        var endpoints = StrokeEndpoints(strokes);
        if (endpoints.Count == 0)
            return Reject(ann.BoundingRect, "no usable stroke points");
        // Evaluate ALL endpoints — don't short-circuit on the first hit.
        // When tail and tip both happen to land inside text leaves, we
        // need to compare and pick the better one (smaller leaf area =
        // tighter target — usually the tip), not whichever the stroke
        // happened to start with.
        IVisualElement? bestLeaf = null;
        var bestDist = double.PositiveInfinity;
        var bestArea = double.PositiveInfinity;
        foreach (var (ex, ey) in endpoints)
        {
            var inLeaf = LeafAtPoint(root, ex, ey);
            if (inLeaf is not null)
            {
                var bb = ToRect(inLeaf.BoundingRectangle);
                var area = RectArea(bb);
                // Inside-leaf hit beats any nearest-leaf candidate (dist=0).
                // Among multiple inside-hits, the tighter leaf wins.
                if (bestDist > 0 || area < bestArea)
                {
                    bestDist = 0; bestArea = area; bestLeaf = inLeaf;
                }
                continue;
            }
            var (nearLeaf, d) = NearestLeaf(root, ex, ey);
            if (nearLeaf is not null && bestDist > 0 && d < bestDist)
            {
                bestDist = d; bestLeaf = nearLeaf;
            }
        }
        if (bestLeaf is null)
        {
            var (cx, cy) = endpoints[0];
            return new SnapResult(
                Rect: new Rect(cx - 30, cy - 30, 60, 60),
                Leaves: [],
                Rejected: true,
                RejectReason: "no text leaf on this screen");
        }
        if (bestDist > 100)
        {
            return new SnapResult(
                Rect: ToRect(bestLeaf.BoundingRectangle),
                Leaves: [],
                Rejected: true,
                RejectReason: $"arrow tip is {(int)bestDist}px from the nearest text — please point closer");
        }
        var conf = bestDist == 0 ? 1.0 : Math.Max(0.4, 1.0 - bestDist / 100);
        return new SnapResult(
            Rect: ToRect(bestLeaf.BoundingRectangle),
            Leaves: [bestLeaf],
            Confidence: conf);
    }

    private static List<(double X, double Y)> StrokeEndpoints(IReadOnlyList<Stroke> strokes)
    {
        var pts = new List<(double, double)>(strokes.Count * 2);
        foreach (var s in strokes)
        {
            if (s.Points.Count == 0) continue;
            var first = s.Points[0];
            var last = s.Points[^1];
            pts.Add((first.X, first.Y));
            pts.Add((last.X, last.Y));
        }
        return pts;
    }

    // -------------------------------------------------------------------
    // Underline: scan upwards from stroke top for the line above
    // -------------------------------------------------------------------

    private static SnapResult SnapUnderline(
        Annotation ann,
        IVisualElement root,
        IReadOnlyList<Stroke> strokes)
    {
        double strokeTop = double.PositiveInfinity, strokeX1 = double.PositiveInfinity, strokeX2 = double.NegativeInfinity;
        foreach (var s in strokes)
        foreach (var p in s.Points)
        {
            if (p.Y < strokeTop) strokeTop = p.Y;
            if (p.X < strokeX1) strokeX1 = p.X;
            if (p.X > strokeX2) strokeX2 = p.X;
        }
        if (double.IsPositiveInfinity(strokeTop))
            return Reject(ann.BoundingRect, "empty stroke");

        var strokeWidth = Math.Max(strokeX2 - strokeX1, 1);
        var candidates = new List<IVisualElement>();
        foreach (var e in Descendants(root))
        {
            if (!LeafTextRoles.Contains(e.Type)) continue;
            var bb = ToRect(e.BoundingRectangle);
            if (bb.Bottom > strokeTop + 5) continue;
            if (strokeTop - bb.Bottom > 80) continue;
            var xInter = Math.Min(bb.Right, strokeX2) - Math.Max(bb.X, strokeX1);
            if (xInter <= 0) continue;
            if (xInter / strokeWidth < 0.5) continue;
            candidates.Add(e);
        }
        if (candidates.Count == 0)
        {
            return new SnapResult(
                Rect: ann.BoundingRect, Leaves: [],
                Rejected: true,
                RejectReason: "no text line above the underline — draw under a line of text");
        }
        // Sort: closest above (smallest gap), then best x-overlap.
        candidates.Sort((a, b) =>
        {
            var gapA = strokeTop - ToRect(a.BoundingRectangle).Bottom;
            var gapB = strokeTop - ToRect(b.BoundingRectangle).Bottom;
            var c = gapA.CompareTo(gapB);
            if (c != 0) return c;
            var oxA = Math.Min(ToRect(a.BoundingRectangle).Right, strokeX2)
                      - Math.Max(ToRect(a.BoundingRectangle).X, strokeX1);
            var oxB = Math.Min(ToRect(b.BoundingRectangle).Right, strokeX2)
                      - Math.Max(ToRect(b.BoundingRectangle).X, strokeX1);
            return oxB.CompareTo(oxA); // larger overlap wins
        });
        var topY = ToRect(candidates[0].BoundingRectangle).Bottom;
        var chosen = candidates
            .Where(c => Math.Abs(ToRect(c.BoundingRectangle).Bottom - topY) < 8)
            .ToList();
        var gapTop = strokeTop - topY;
        var conf = Math.Max(0.4, 1.0 - gapTop / 60);
        return new SnapResult(
            Rect: AdjustRectToLeaves(ann.BoundingRect, chosen),
            Leaves: chosen,
            Confidence: conf);
    }

    // -------------------------------------------------------------------
    // Circle / X: center-in-rect collection
    // -------------------------------------------------------------------

    private static SnapResult SnapCircleOrX(Annotation ann, IVisualElement root)
    {
        var leaves = new List<IVisualElement>();
        foreach (var e in Descendants(root))
        {
            if (!LeafTextRoles.Contains(e.Type)) continue;
            var bb = ToRect(e.BoundingRectangle);
            // Strict containment: the leaf must be FULLY inside the gesture
            // rect. Avoids picking up the line above/below when the user's
            // circle was drawn slightly larger than the visual content —
            // their visual intent is the line whose entire bbox sits inside
            // the circle, not lines whose centerline merely happens to be
            // grazed by the rect's edge.
            if (bb.Y >= ann.BoundingRect.Y
                && bb.Bottom <= ann.BoundingRect.Bottom
                && bb.X >= ann.BoundingRect.X - 4   // slight horizontal slack
                && bb.Right <= ann.BoundingRect.Right + 4)
            {
                leaves.Add(e);
            }
        }
        if (leaves.Count == 0)
        {
            // Fallback 1: leaf has >=50% vertical overlap with the rect.
            // Better than 'center inside': handles slightly mis-drawn
            // gestures that miss the visual center but still cover most
            // of the line.
            foreach (var e in Descendants(root))
            {
                if (!LeafTextRoles.Contains(e.Type)) continue;
                var bb = ToRect(e.BoundingRectangle);
                var inter = Math.Min(bb.Bottom, ann.BoundingRect.Bottom)
                            - Math.Max(bb.Y, ann.BoundingRect.Y);
                if (inter <= 0 || bb.Height <= 0) continue;
                if (inter / bb.Height < 0.5) continue;
                // Horizontal: require x-overlap so a stray gesture in the
                // far margin doesn't grab a line on the same y band.
                var xInter = Math.Min(bb.Right, ann.BoundingRect.Right)
                             - Math.Max(bb.X, ann.BoundingRect.X);
                if (xInter <= 0) continue;
                leaves.Add(e);
            }
        }
        if (leaves.Count == 0)
        {
            // Fallback 2: 50% overlap.
            foreach (var e in Descendants(root))
            {
                if (!LeafTextRoles.Contains(e.Type)) continue;
                if (OverlapRatio(ToRect(e.BoundingRectangle), ann.BoundingRect) >= 0.5)
                    leaves.Add(e);
            }
        }
        if (leaves.Count == 0)
        {
            var (nearest, dist) = NearestLeaf(root, ann.BoundingRect.Center.X, ann.BoundingRect.Center.Y);
            if (nearest is null || dist > 120)
            {
                return new SnapResult(
                    Rect: ann.BoundingRect, Leaves: [],
                    Rejected: true,
                    RejectReason: "no text inside the gesture — please redraw closer to the content");
            }
            return new SnapResult(
                Rect: ToRect(nearest.BoundingRectangle), Leaves: [nearest],
                Confidence: Math.Max(0.3, 1.0 - dist / 120));
        }
        // Sanity: leaves area shouldn't massively exceed gesture bbox.
        var leafArea = leaves.Sum(lf => RectArea(ToRect(lf.BoundingRectangle)));
        var annArea = RectArea(ann.BoundingRect);
        if (leafArea > annArea * 4 && leaves.Count > 8)
        {
            return new SnapResult(
                Rect: ann.BoundingRect, Leaves: [],
                Rejected: true,
                RejectReason: $"gesture is too small for {leaves.Count} text elements — please redraw");
        }
        return new SnapResult(
            Rect: AdjustRectToLeaves(ann.BoundingRect, leaves),
            Leaves: leaves);
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private static SnapResult Reject(Rect rect, string reason) =>
        new(rect, [], Rejected: true, RejectReason: reason);

    private static IVisualElement? LeafAtPoint(IVisualElement root, double x, double y)
    {
        IVisualElement? best = null;
        var bestArea = double.PositiveInfinity;
        foreach (var e in Descendants(root))
        {
            if (!LeafTextRoles.Contains(e.Type)) continue;
            var bb = ToRect(e.BoundingRectangle);
            if (x < bb.X || x > bb.Right || y < bb.Y || y > bb.Bottom) continue;
            var area = RectArea(bb);
            if (area < bestArea) { bestArea = area; best = e; }
        }
        return best;
    }

    private static (IVisualElement? leaf, double dist) NearestLeaf(
        IVisualElement root, double x, double y)
    {
        IVisualElement? best = null;
        var bestD = double.PositiveInfinity;
        foreach (var e in Descendants(root))
        {
            if (!LeafTextRoles.Contains(e.Type)) continue;
            var bb = ToRect(e.BoundingRectangle);
            var dx = Math.Max(Math.Max(bb.X - x, 0), x - bb.Right);
            var dy = Math.Max(Math.Max(bb.Y - y, 0), y - bb.Bottom);
            var d = Math.Sqrt(dx * dx + dy * dy);
            if (d < bestD) { bestD = d; best = e; }
        }
        return (best, bestD);
    }

    private static Rect AdjustRectToLeaves(Rect rect, IReadOnlyList<IVisualElement> leaves,
                                            double pad = 4.0)
    {
        if (leaves.Count == 0) return rect;
        double x1 = double.PositiveInfinity, y1 = double.PositiveInfinity;
        double x2 = double.NegativeInfinity, y2 = double.NegativeInfinity;
        foreach (var lf in leaves)
        {
            var bb = ToRect(lf.BoundingRectangle);
            if (bb.X < x1) x1 = bb.X;
            if (bb.Y < y1) y1 = bb.Y;
            if (bb.Right > x2) x2 = bb.Right;
            if (bb.Bottom > y2) y2 = bb.Bottom;
        }
        return new Rect(x1 - pad, y1 - pad, x2 - x1 + 2 * pad, y2 - y1 + 2 * pad);
    }

    private static double OverlapRatio(Rect of, Rect against)
    {
        var inter = of.Intersect(against);
        var area = RectArea(of);
        return area > 0 ? RectArea(inter) / area : 0.0;
    }

    private static double RectArea(Rect r) => Math.Max(0, r.Width) * Math.Max(0, r.Height);

    private static Rect ToRect(PixelRect pr) => new(pr.X, pr.Y, pr.Width, pr.Height);

    private static IEnumerable<IVisualElement> Descendants(IVisualElement root)
    {
        yield return root;
        foreach (var c in root.Children)
        foreach (var d in Descendants(c))
            yield return d;
    }
}
