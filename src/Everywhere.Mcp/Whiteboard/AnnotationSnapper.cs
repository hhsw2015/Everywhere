using System.Globalization;
using System.Text;
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

    /// <summary>
    /// Circle and X gestures may target images too — users circle photos
    /// and diagrams as often as text. Arrow/Underline keep the strict
    /// text-only set because pointing at or underlining a Label has a
    /// concrete meaning while pointing at an image is ambiguous.
    /// </summary>
    private static readonly HashSet<VisualElementType> LeafTextOrImageRoles =
    [
        VisualElementType.Label,
        VisualElementType.Hyperlink,
        VisualElementType.Image,
    ];

    public static SnapResult Snap(
        Annotation ann,
        IVisualElement root,
        IReadOnlyList<Stroke> strokes,
        PrewarmedTree? prewarmed = null)
    {
        return ann.Kind switch
        {
            AnnotationKind.Arrow => SnapArrow(ann, root, strokes, prewarmed),
            AnnotationKind.Underline => SnapUnderline(ann, root, strokes, prewarmed),
            AnnotationKind.Circle => SnapCircleOrX(ann, root, prewarmed),
            AnnotationKind.X => SnapCircleOrX(ann, root, prewarmed),
            _ => Reject(ann.BoundingRect, "unknown gesture kind"),
        };
    }

    // Walk source: cache when available (no live AX calls), else live tree.
    private static IEnumerable<(IVisualElement Node, Rect Bbox)> Walk(
        IVisualElement root, Rect query, PrewarmedTree? prewarmed, double slack = 8.0)
    {
        if (prewarmed is not null) return prewarmed.QueryRect(query, slack);
        return DescendantsInRect(root, query, slack);
    }

    // -------------------------------------------------------------------
    // Arrow: tip-point lookup
    // -------------------------------------------------------------------

    private static SnapResult SnapArrow(
        Annotation ann,
        IVisualElement root,
        IReadOnlyList<Stroke> strokes,
        PrewarmedTree? prewarmed)
    {
        var diag = new StringBuilder();
        diag.Append("arrow: ");
        var endpoints = StrokeEndpoints(strokes);
        if (endpoints.Count == 0)
            return Reject(ann.BoundingRect, "no usable stroke points");
        diag.Append("endpoints=[")
            .AppendJoin(",", endpoints.Select(e => F(e.X, e.Y)))
            .Append("] ");
        // Identify the actual arrow tip from geometry. For a 2-stroke
        // arrow (axis + V head), the tip is the axis endpoint nearest
        // the head V's centre — the head sits AT the tip. For a 1-
        // stroke arrow we don't have a head to triangulate; tipHint is
        // null and we fall back to the prior "smallest leaf area"
        // heuristic across all endpoints.
        var tipHint = IdentifyArrowTip(strokes);
        if (tipHint is { } tip)
        {
            // Prefer the leaf at the tip endpoint when one exists,
            // regardless of leaf area. This avoids the failure mode
            // where the arrow's TAIL happens to sit inside a small
            // leaf ("2 days ago") and outranks the tip's larger leaf
            // (commit body description) just because area-as-tightness
            // is a poor proxy for user intent.
            diag.Append($"tipHint={F(tip.X, tip.Y)} ");
            var tipLeaf = LeafAtPoint(root, tip.X, tip.Y, prewarmed);
            if (tipLeaf is null && prewarmed is not null)
                tipLeaf = LeafAtPoint(root, tip.X, tip.Y, prewarmed: null);
            if (tipLeaf is not null)
            {
                var bb = ToRect(tipLeaf.BoundingRectangle);
                diag.Append($"tipLeaf=\"{Trunc(tipLeaf.GetText())}\" ");
                return new SnapResult(
                    Rect: TightenLeafToTip(tipLeaf, tip.X, tip.Y),
                    Leaves: [tipLeaf],
                    Confidence: 1.0,
                    Diagnostics: diag.ToString());
            }
            // No leaf at tip — try a NearestLeaf at tip first before
            // falling through to the all-endpoints heuristic.
            var (tipNear, tipDist) = NearestLeaf(root, tip.X, tip.Y, prewarmed);
            if (tipNear is null && prewarmed is not null)
                (tipNear, tipDist) = NearestLeaf(root, tip.X, tip.Y, prewarmed: null);
            if (tipNear is not null && tipDist <= 100)
            {
                diag.Append($"tipNear=\"{Trunc(tipNear.GetText())}\" d={tipDist:F0} ");
                var tipConf = Math.Max(0.4, 1.0 - tipDist / 100);
                return new SnapResult(
                    Rect: TightenLeafToTip(tipNear, tip.X, tip.Y),
                    Leaves: [tipNear],
                    Confidence: tipConf,
                    Diagnostics: diag.ToString());
            }
        }
        // Fall-through path (1-stroke arrow or tipHint produced nothing
        // usable): evaluate ALL endpoints, prefer in-leaf, smallest area.
        IVisualElement? bestLeaf = null;
        var bestDist = double.PositiveInfinity;
        var bestArea = double.PositiveInfinity;
        var foundInLeaf = false;
        double tipX = endpoints[0].X, tipY = endpoints[0].Y;
        foreach (var (ex, ey) in endpoints)
        {
            var inLeaf = LeafAtPoint(root, ex, ey, prewarmed);
            var inWalk = s_lastWalkVisited;
            // Prewarm-miss fallback: prewarm caps at 25k visited nodes,
            // so on long pages a real leaf may sit past the cap and be
            // absent from prewarm.Nodes. Retry with a live walk for
            // this single point — query rect is 5×5, prune is effective,
            // typical cost <50ms.
            if (inLeaf is null && prewarmed is not null)
            {
                inLeaf = LeafAtPoint(root, ex, ey, prewarmed: null);
                if (inLeaf is not null) inWalk = s_lastWalkVisited;
            }
            if (inLeaf is not null)
            {
                var bb = ToRect(inLeaf.BoundingRectangle);
                var area = RectArea(bb);
                diag.Append($"in@{F(ex, ey)}[w={inWalk}]->\"{Trunc(inLeaf.GetText())}\" ");
                // In-leaf hit always beats any nearest-leaf candidate.
                // Among multiple in-leaf hits, the tighter (smaller area)
                // leaf wins — usually the arrow's tip end.
                if (!foundInLeaf || area < bestArea)
                {
                    foundInLeaf = true; bestDist = 0; bestArea = area;
                    bestLeaf = inLeaf; tipX = ex; tipY = ey;
                }
                continue;
            }
            diag.Append($"miss@{F(ex, ey)}[w={inWalk}] ");
            if (foundInLeaf) continue;
            var (nearLeaf, d) = NearestLeaf(root, ex, ey, prewarmed);
            var nearWalk = s_lastWalkVisited;
            // Prewarm-miss fallback for NearestLeaf — only when prewarm
            // returned nothing at all. Don't fall back on "got a leaf
            // with empty text" — that's a legitimate case (anchor
            // wrapping an image) and would force a redundant 240×240
            // live walk on every commit.
            if (nearLeaf is null && prewarmed is not null)
            {
                var (liveNear, liveDist) = NearestLeaf(root, ex, ey, prewarmed: null);
                if (liveNear is not null)
                {
                    nearLeaf = liveNear; d = liveDist; nearWalk = s_lastWalkVisited;
                }
            }
            if (nearLeaf is not null)
            {
                diag.Append($"near@{F(ex, ey)}[w={nearWalk}]->\"{Trunc(nearLeaf.GetText())}\" d={d:F0} ");
                if (d < bestDist)
                {
                    bestDist = d; bestLeaf = nearLeaf; tipX = ex; tipY = ey;
                }
            }
        }
        if (bestLeaf is null)
        {
            var (cx, cy) = endpoints[0];
            return new SnapResult(
                Rect: new Rect(cx - 30, cy - 30, 60, 60),
                Leaves: [],
                Rejected: true,
                RejectReason: "no text leaf on this screen",
                Diagnostics: diag.ToString());
        }
        if (bestDist > 100)
        {
            return new SnapResult(
                Rect: TightenLeafToTip(bestLeaf, tipX, tipY),
                Leaves: [],
                Rejected: true,
                RejectReason: $"arrow tip is {(int)bestDist}px from the nearest text — please point closer",
                Diagnostics: diag.ToString());
        }
        var conf = bestDist == 0 ? 1.0 : Math.Max(0.4, 1.0 - bestDist / 100);
        return new SnapResult(
            Rect: TightenLeafToTip(bestLeaf, tipX, tipY),
            Leaves: [bestLeaf],
            Confidence: conf,
            Diagnostics: diag.ToString());
    }

    // For a 2-stroke arrow (axis + head V), returns the axis endpoint that
    // sits nearest the head V's centre — that's the tip in screen space.
    // The head wraps the tip, so the head V's chord-midpoint is a proxy
    // for the tip's location, and the axis endpoint closer to that
    // midpoint is the tip end of the axis. Returns null for 1-stroke
    // arrows (caller falls back to its older smallest-leaf heuristic).
    private static double Sq(double v) => v * v;

    private static (double X, double Y)? IdentifyArrowTip(IReadOnlyList<Stroke> strokes)
    {
        if (strokes.Count != 2) return null;
        // Stroke roles aren't tagged; pick by length — axis is the longer
        // chord, head is the shorter one (LooksLikeArrow already enforced
        // ratio ≤ 0.70 so this is unambiguous).
        var p1 = strokes[0].Points;
        var p2 = strokes[1].Points;
        if (p1.Count < 2 || p2.Count < 2) return null;
        var len1 = Math.Sqrt(Sq(p1[^1].X - p1[0].X) + Sq(p1[^1].Y - p1[0].Y));
        var len2 = Math.Sqrt(Sq(p2[^1].X - p2[0].X) + Sq(p2[^1].Y - p2[0].Y));
        var axis = len1 >= len2 ? p1 : p2;
        var head = len1 >= len2 ? p2 : p1;
        var headMidX = (head[0].X + head[^1].X) * 0.5;
        var headMidY = (head[0].Y + head[^1].Y) * 0.5;
        var d0 = Sq(axis[0].X - headMidX) + Sq(axis[0].Y - headMidY);
        var d1 = Sq(axis[^1].X - headMidX) + Sq(axis[^1].Y - headMidY);
        return d0 <= d1 ? (axis[0].X, axis[0].Y) : (axis[^1].X, axis[^1].Y);
    }

    // When a leaf bbox is much taller than a single text row (Arc/Chromium
    // expose multi-line commit body, lyric block, etc. as one Label of
    // ~250×500px), returning the whole leaf bbox dumps the entire block
    // even though the user pointed at one line. Crop the leaf bbox to a
    // ~row-tall slice centred on the arrow tip's Y. Width = full leaf
    // width (a row spans the full block horizontally). Height fudge: 32px
    // — covers most font sizes, < the 50px "huge leaf" threshold below.
    private static Rect TightenLeafToTip(IVisualElement leaf, double tipX, double tipY)
    {
        var bb = ToRect(leaf.BoundingRectangle);
        // Small leaves (single-line labels, hyperlinks): return as-is.
        if (bb.Height <= 50) return bb;
        const double rowH = 32.0;
        var top = Math.Max(bb.Y, tipY - rowH / 2);
        var bottom = Math.Min(bb.Bottom, tipY + rowH / 2);
        if (bottom <= top) return bb;
        return new Rect(bb.X, top, bb.Width, bottom - top);
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
        IReadOnlyList<Stroke> strokes,
        PrewarmedTree? prewarmed)
    {
        double strokeTop = double.PositiveInfinity, strokeBottom = double.NegativeInfinity,
               strokeX1 = double.PositiveInfinity, strokeX2 = double.NegativeInfinity;
        foreach (var s in strokes)
        foreach (var p in s.Points)
        {
            if (p.Y < strokeTop) strokeTop = p.Y;
            if (p.Y > strokeBottom) strokeBottom = p.Y;
            if (p.X < strokeX1) strokeX1 = p.X;
            if (p.X > strokeX2) strokeX2 = p.X;
        }
        if (double.IsPositiveInfinity(strokeTop))
            return Reject(ann.BoundingRect, "empty stroke");

        var diag = new StringBuilder();
        diag.Append("underline: ")
            .Append($"strokeTop={strokeTop:F0} strokeBottom={strokeBottom:F0} ")
            .Append($"strokeX=[{strokeX1:F0},{strokeX2:F0}] ");

        var strokeWidth = Math.Max(strokeX2 - strokeX1, 1);
        var (above, aboveDiag) = CollectUnderlineCandidatesV(
            root, strokeTop, strokeX1, strokeX2, strokeWidth, above: true, prewarmed);
        diag.Append("above=").Append(aboveDiag).Append(' ');
        // Prewarm-miss fallback: if prewarm returned no candidates but
        // a prewarm IS in use, retry once against the live tree. The
        // query rect is small (strokeWidth × ~110) so the live walk is
        // bounded — typical cost <300ms on Chromium.
        if (above.Count == 0 && prewarmed is not null)
        {
            var (aboveLive, aboveLiveDiag) = CollectUnderlineCandidatesV(
                root, strokeTop, strokeX1, strokeX2, strokeWidth, above: true, prewarmed: null);
            diag.Append("aboveLive=").Append(aboveLiveDiag).Append(' ');
            above = aboveLive;
        }
        var candidates = above;
        var pickedSide = UnderlineSide.Above;
        // Tolerate users who draw the line ABOVE the text (overline-style
        // emphasis) — fall back to looking for a line just below the
        // stroke when nothing is above.
        if (candidates.Count == 0)
        {
            var (below, belowDiag) = CollectUnderlineCandidatesV(
                root, strokeBottom, strokeX1, strokeX2, strokeWidth, above: false, prewarmed);
            diag.Append("below=").Append(belowDiag);
            if (below.Count == 0 && prewarmed is not null)
            {
                var (belowLive, belowLiveDiag) = CollectUnderlineCandidatesV(
                    root, strokeBottom, strokeX1, strokeX2, strokeWidth, above: false, prewarmed: null);
                diag.Append(" belowLive=").Append(belowLiveDiag);
                below = belowLive;
            }
            candidates = below;
            pickedSide = UnderlineSide.Below;
        }
        if (candidates.Count == 0)
        {
            return new SnapResult(
                Rect: ann.BoundingRect, Leaves: [],
                Rejected: true,
                RejectReason: "no text line near the underline — draw the line directly above or below a line of text",
                Diagnostics: diag.ToString());
        }
        // Sort: closest gap (above OR below depending on which path matched),
        // then best x-overlap.
        var anchor = pickedSide == UnderlineSide.Above ? strokeTop : strokeBottom;
        var bands = candidates
            .Select(c => (Leaf: c,
                          EdgeY: pickedSide == UnderlineSide.Above
                              ? ToRect(c.BoundingRectangle).Bottom
                              : ToRect(c.BoundingRectangle).Y))
            .ToList();
        bands.Sort((a, b) =>
        {
            var gapA = Math.Abs(anchor - a.EdgeY);
            var gapB = Math.Abs(anchor - b.EdgeY);
            var c = gapA.CompareTo(gapB);
            if (c != 0) return c;
            var oxA = Math.Min(ToRect(a.Leaf.BoundingRectangle).Right, strokeX2)
                      - Math.Max(ToRect(a.Leaf.BoundingRectangle).X, strokeX1);
            var oxB = Math.Min(ToRect(b.Leaf.BoundingRectangle).Right, strokeX2)
                      - Math.Max(ToRect(b.Leaf.BoundingRectangle).X, strokeX1);
            return oxB.CompareTo(oxA);
        });
        var topY = bands[0].EdgeY;
        var chosen = bands
            .Where(c => Math.Abs(c.EdgeY - topY) < 8)
            .Select(c => c.Leaf)
            .ToList();
        var gapTop = Math.Abs(anchor - topY);
        var conf = Math.Max(0.4, 1.0 - gapTop / 60);
        return new SnapResult(
            Rect: AdjustRectToLeaves(ann.BoundingRect, chosen),
            Leaves: chosen,
            Confidence: conf,
            Diagnostics: diag.ToString());
    }

    private enum UnderlineSide { Above, Below }

    private static (List<IVisualElement>, string) CollectUnderlineCandidatesV(
        IVisualElement root,
        double strokeY, double strokeX1, double strokeX2, double strokeWidth,
        bool above, PrewarmedTree? prewarmed)
    {
        var list = new List<IVisualElement>();
        int seen = 0, failedSide = 0, failedGap = 0, failedXBand = 0, failedXRatio = 0;
        int totalWalked = 0;
        // Underline considers leaves up to 80px above/below the stroke and
        // within its x-band — bound the walk to that rect.
        var queryRect = above
            ? new Rect(strokeX1, strokeY - 80 - 15, strokeWidth, 80 + 30)
            : new Rect(strokeX1, strokeY - 15, strokeWidth, 80 + 30);
        foreach (var (e, bb) in Walk(root, queryRect, prewarmed))
        {
            // Hard cap on total walked nodes. v0.9.216 diag showed
            // Chromium webview can deliver 76k+ Label nodes in a single
            // CollectUnderlineCandidates pass (deeply nested div soup);
            // beyond ~5k the candidate set isn't getting better, just
            // longer. Bail rather than freeze.
            totalWalked++;
            if (totalWalked > 5000) break;
            if (!LeafTextRoles.Contains(e.Type)) continue;
            seen++;
            // Tolerate small jitter — leaf may extend slightly past the
            // stroke's top/bottom edge when the user grazed the text. 15px
            // covers font ascender/descender + light hand jitter.
            if (above)
            {
                if (bb.Bottom > strokeY + 15) { failedSide++; continue; }
                if (strokeY - bb.Bottom > 80) { failedGap++; continue; }
            }
            else
            {
                if (bb.Y < strokeY - 15) { failedSide++; continue; }
                if (bb.Y - strokeY > 80) { failedGap++; continue; }
            }
            var xInter = Math.Min(bb.Right, strokeX2) - Math.Max(bb.X, strokeX1);
            if (xInter <= 0) { failedXBand++; continue; }
            // Primary: stroke must cover ≥50% of itself with the leaf's
            // x-band — a 30px tick under a 500px line is NOT an underline.
            // Secondary: when the leaf is much shorter than the stroke
            // (e.g. user drew long line, leaf is a single short link),
            // accept if the leaf is mostly inside the stroke's band.
            var ratioVsStroke = xInter / strokeWidth;
            var leafW = Math.Max(1, bb.Width);
            var ratioVsLeaf = xInter / leafW;
            var shortLeaf = bb.Width < strokeWidth * 0.5;
            if (ratioVsStroke < 0.5 && !(shortLeaf && ratioVsLeaf >= 0.5)) { failedXRatio++; continue; }
            list.Add(e);
        }
        var diag =
            $"[seen={seen} kept={list.Count} failedSide={failedSide} " +
            $"failedGap={failedGap} failedXBand={failedXBand} failedXRatio={failedXRatio}]";
        return (list, diag);
    }

    // -------------------------------------------------------------------
    // Circle / X: center-in-rect collection
    // -------------------------------------------------------------------

    private static SnapResult SnapCircleOrX(Annotation ann, IVisualElement root, PrewarmedTree? prewarmed)
    {
        var diag = new StringBuilder();
        diag.Append("circle/x: rect=").Append(F(ann.BoundingRect)).Append(' ');
        var leaves = new List<IVisualElement>();
        int totalLeaves = 0;
        int walked = 0;
        foreach (var (e, bb) in Walk(root, ann.BoundingRect, prewarmed))
        {
            walked++; if (walked > 5000) break;
            if (!LeafTextOrImageRoles.Contains(e.Type)) continue;
            totalLeaves++;
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
        diag.Append($"totalLeaves={totalLeaves} pass1Strict={leaves.Count} ");
        if (leaves.Count == 0)
        {
            // Fallback 1: leaf has >=50% vertical overlap with the rect.
            // Better than 'center inside': handles slightly mis-drawn
            // gestures that miss the visual center but still cover most
            // of the line.
            int walked2 = 0;
            foreach (var (e, bb) in Walk(root, ann.BoundingRect, prewarmed))
            {
                walked2++; if (walked2 > 5000) break;
                if (!LeafTextOrImageRoles.Contains(e.Type)) continue;
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
            diag.Append($"pass2VOverlap={leaves.Count} ");
        }
        if (leaves.Count == 0)
        {
            // Fallback 2: 50% overlap.
            int walked3 = 0;
            foreach (var (e, bb) in Walk(root, ann.BoundingRect, prewarmed))
            {
                walked3++; if (walked3 > 5000) break;
                if (!LeafTextOrImageRoles.Contains(e.Type)) continue;
                if (OverlapRatio(bb, ann.BoundingRect) >= 0.5)
                    leaves.Add(e);
            }
            diag.Append($"pass3Area={leaves.Count} ");
        }
        if (leaves.Count == 0)
        {
            var (nearest, dist) = NearestLeaf(root, ann.BoundingRect.Center.X, ann.BoundingRect.Center.Y);
            diag.Append($"nearest=\"{Trunc(nearest?.GetText())}\" d={dist:F0} ");
            if (nearest is null || dist > 120)
            {
                return new SnapResult(
                    Rect: ann.BoundingRect, Leaves: [],
                    Rejected: true,
                    RejectReason: "no text inside the gesture — please redraw closer to the content",
                    Diagnostics: diag.ToString());
            }
            return new SnapResult(
                Rect: ToRect(nearest.BoundingRectangle), Leaves: [nearest],
                Confidence: Math.Max(0.3, 1.0 - dist / 120),
                Diagnostics: diag.ToString());
        }
        // Sanity: leaves area shouldn't massively exceed gesture bbox.
        // Exclude Image leaves from the area sanity check — image
        // bboxes are typically much larger than text labels, so a row
        // of thumbnails would trip this gate even when the gesture
        // intentionally circles all of them.
        var leafArea = leaves
            .Where(lf => lf.Type != VisualElementType.Image)
            .Sum(lf => RectArea(ToRect(lf.BoundingRectangle)));
        var annArea = RectArea(ann.BoundingRect);
        if (leafArea > annArea * 4 && leaves.Count > 8)
        {
            return new SnapResult(
                Rect: ann.BoundingRect, Leaves: [],
                Rejected: true,
                RejectReason: $"gesture is too small for {leaves.Count} text elements — please redraw",
                Diagnostics: diag.ToString());
        }
        return new SnapResult(
            Rect: AdjustRectToLeaves(ann.BoundingRect, leaves),
            Leaves: leaves,
            Diagnostics: diag.ToString());
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private static SnapResult Reject(Rect rect, string reason) =>
        new(rect, [], Rejected: true, RejectReason: reason);

    private static IVisualElement? LeafAtPoint(
        IVisualElement root, double x, double y, PrewarmedTree? prewarmed = null)
    {
        var pointRect = new Rect(x, y, 1, 1);
        var visited = 0;
        IVisualElement? hit = null;
        foreach (var (e, bb) in Walk(root, pointRect, prewarmed, slack: 2.0))
        {
            visited++;
            // Hard cap defends against degenerate trees where prune fails.
            if (visited > 5000) break;
            if (!LeafTextRoles.Contains(e.Type)) continue;
            if (x < bb.X || x > bb.Right || y < bb.Y || y > bb.Bottom) continue;
            // First leaf-role node containing the point wins. DFS pre-order
            // means we visit ancestors before descendants, so the first hit
            // is the SHALLOWEST leaf containing the point. We don't need
            // "smallest area" — for a 1×1 query, any leaf containing it is
            // already a precise match, and walking further to find a tighter
            // sibling costs thousands of node visits on Chromium webviews
            // where a single page region holds deep wrapper chains.
            hit = e;
            break;
        }
        s_lastWalkVisited = visited;
        return hit;
    }

    // Cross-call diagnostic counter — Snap reads & resets it after each
    // LeafAtPoint to surface walk size in the snap diag log line.
    [ThreadStatic] private static int s_lastWalkVisited;

    private static (IVisualElement? leaf, double dist) NearestLeaf(
        IVisualElement root, double x, double y, PrewarmedTree? prewarmed = null)
    {
        // The single caller rejects matches with d > 120 (SnapCircleOrX
        // fallback path). Bound the walk to a 240×240 box around the point
        // — anything farther can't possibly improve on bestD.
        IVisualElement? best = null;
        var bestD = double.PositiveInfinity;
        var pointRect = new Rect(x - 120, y - 120, 240, 240);
        var visited = 0;
        foreach (var (e, bb) in Walk(root, pointRect, prewarmed, slack: 0.0))
        {
            visited++;
            if (visited > 5000) break;  // bail on degenerate AX trees
            if (!LeafTextRoles.Contains(e.Type)) continue;
            var dx = Math.Max(Math.Max(bb.X - x, 0), x - bb.Right);
            var dy = Math.Max(Math.Max(bb.Y - y, 0), y - bb.Bottom);
            var d = Math.Sqrt(dx * dx + dy * dy);
            if (d < bestD) { bestD = d; best = e; }
        }
        s_lastWalkVisited = visited;
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

    // -----------------------------------------------------------------
    // Prewarmed tree — flat snapshot of every node visited from the root
    // taken once during overlay-show idle time. Snap routines query this
    // by rect intersection instead of doing live AX walks at commit time,
    // dropping perceived snap latency on Chromium webviews (where each
    // live walk takes 5–15s) to whatever the user already spent drawing
    // their gesture.
    // -----------------------------------------------------------------
    public sealed class PrewarmedTree
    {
        // Flat list, DFS pre-order to preserve "first hit wins" semantics
        // some callers rely on (LeafAtPoint, NearestLeaf already use rect
        // pruning + cap on top).
        public List<(IVisualElement Node, Rect Bbox)> Nodes { get; }
        // Empty-text Hyperlinks that DO have an Image descendant. Computed
        // once during Build() so image-collect doesn't need to retraverse
        // the tree at commit time.
        public HashSet<IVisualElement> HyperlinkHasImage { get; }
        // Empty-text Hyperlinks that have a text-bearing Label descendant
        // (treated as a text-link, not as image-as-link).
        public HashSet<IVisualElement> HyperlinkHasText { get; }
        // True when Build() bailed at the node cap. Callers should treat
        // hyperlinkHasImage/hyperlinkHasText as POTENTIALLY incomplete and
        // either fall back to a live precompute or accept degraded image
        // classification for image-collect.
        public bool CapHit { get; }
        public PrewarmedTree(
            List<(IVisualElement, Rect)> nodes,
            HashSet<IVisualElement> hyperlinkHasImage,
            HashSet<IVisualElement> hyperlinkHasText,
            bool capHit)
        {
            Nodes = nodes;
            HyperlinkHasImage = hyperlinkHasImage;
            HyperlinkHasText = hyperlinkHasText;
            CapHit = capHit;
        }

        // Build the flat node list AND the hyperlink-has-{image,text}
        // ancestor sets in a single rect-pruned walk. Cap is small (15k)
        // so the walk completes in 2.5–3.5s on the worst Arc commit
        // pages observed — well under the commit-time await window.
        // CancellationToken lets the caller bail.
        public static PrewarmedTree Build(IVisualElement root, CancellationToken ct)
        {
            // Constrain the walk to the focused window's own bounds. Off-
            // screen subtrees (long pages with virtualised content above /
            // below the viewport) get pruned at parent level, freeing the
            // 25k node budget for content the user can actually gesture
            // on. Empty-bbox wrappers still recurse — the prune predicate
            // is "child has real bbox AND it's outside viewport".
            Rect rootBb;
            try { rootBb = ToRect(root.BoundingRectangle); }
            catch { rootBb = default; }
            var viewport = (rootBb.Width > 0 && rootBb.Height > 0)
                ? rootBb
                : new Rect(-1e9, -1e9, 2e9, 2e9);
            var nodes = new List<(IVisualElement, Rect)>(4096);
            var visited = new HashSet<IVisualElement>(ReferenceEqualityComparer.Instance);
            var hyperlinkHasImage = new HashSet<IVisualElement>(ReferenceEqualityComparer.Instance);
            var hyperlinkHasText = new HashSet<IVisualElement>(ReferenceEqualityComparer.Instance);
            var capHit = false;
            BuildImpl(root, rootBb, viewport, nodes, visited,
                hyperlinkHasImage, hyperlinkHasText,
                ancestorHyperlink: null, cap: 25_000, ref capHit, ct);
            return new PrewarmedTree(nodes, hyperlinkHasImage, hyperlinkHasText, capHit);
        }

        private static void BuildImpl(
            IVisualElement node, Rect nodeBb, Rect expanded,
            List<(IVisualElement, Rect)> sink,
            HashSet<IVisualElement> visited,
            HashSet<IVisualElement> hyperlinkHasImage,
            HashSet<IVisualElement> hyperlinkHasText,
            IVisualElement? ancestorHyperlink,
            int cap, ref bool capHit, CancellationToken ct)
        {
            // Cap on TOTAL visited (not sink size) — Arc has ~80k total nodes
            // but only ~2k are leaf-role; capping by sink would let the walk
            // run uncapped through wrappers.
            if (visited.Count >= cap) { capHit = true; return; }
            if (ct.IsCancellationRequested) return;
            if (!visited.Add(node)) return;

            // Only emit leaf-role nodes (Label / Hyperlink / Image) into
            // Nodes. Snap callers only ever filter by these roles, so
            // emitting wrappers / Document / Group / etc. just bloats the
            // list and inflates per-call iteration cost.
            if (LeafTextRoles.Contains(node.Type)
                || LeafTextOrImageRoles.Contains(node.Type))
                sink.Add((node, nodeBb));

            // Track empty-text Hyperlinks for the image-collect phase.
            // Mirrors the precompute walk in WhiteboardHotkeyInitializer's
            // CollectImageLeavesWithDiag.
            var nextAncestor = ancestorHyperlink;
            try
            {
                if (node.Type == VisualElementType.Hyperlink && nextAncestor is null
                    && string.IsNullOrWhiteSpace(node.GetText(maxLength: 1)))
                {
                    nextAncestor = node;
                }
                if (ancestorHyperlink is not null)
                {
                    if (node.Type == VisualElementType.Image)
                        hyperlinkHasImage.Add(ancestorHyperlink);
                    else if (node.Type == VisualElementType.Label
                             && !string.IsNullOrWhiteSpace(node.GetText(maxLength: 1)))
                        hyperlinkHasText.Add(ancestorHyperlink);
                }
            }
            catch { /* GetText may fail on transient AX nodes — ignore */ }

            // Don't recurse into Label or Image — their children are
            // per-glyph wrappers that explode the walk count and add
            // nothing meaningful. DO recurse into Hyperlink, even though
            // it is leaf-role: image-collect needs to discover Image
            // descendants nested inside <a><img></a> anchors and
            // hyperlinkHasImage is populated in the recursion below.
            if (node.Type == VisualElementType.Label
                || node.Type == VisualElementType.Image)
                return;
            foreach (var c in node.Children)
            {
                if (ct.IsCancellationRequested) return;
                Rect cBb;
                try { cBb = ToRect(c.BoundingRectangle); }
                catch { cBb = default; }
                if (cBb.Width > 0 && cBb.Height > 0)
                {
                    var inter = cBb.Intersect(expanded);
                    if (inter.Width <= 0 || inter.Height <= 0) continue;
                }
                BuildImpl(c, cBb, expanded, sink, visited,
                    hyperlinkHasImage, hyperlinkHasText,
                    nextAncestor, cap, ref capHit, ct);
            }
        }

        // Filter the flat list to nodes whose bbox intersects query+slack.
        // For empty-bbox nodes (Chromium wrappers) we include them so the
        // body's role-based filter still has a chance — same semantics as
        // the live walk. The yield cap mirrors live-walk semantics: snap
        // callers each have their own 5000-visit cap, but on a heavily-
        // dense webview the prewarm could plausibly include >5000 leaves,
        // so we cap at the same shape here too.
        public IEnumerable<(IVisualElement Node, Rect Bbox)> QueryRect(
            Rect query, double slack = 8.0, int maxYield = 5000)
        {
            var expanded = new Rect(
                query.X - slack, query.Y - slack,
                query.Width + 2 * slack, query.Height + 2 * slack);
            var yielded = 0;
            foreach (var n in Nodes)
            {
                if (yielded >= maxYield) yield break;
                var bb = n.Bbox;
                // Skip zero-bbox nodes — they can't satisfy any geometric
                // test snap callers do. The live walk yields them too
                // (DescendantsInRect needs them for prune decisions on
                // children) but Nodes here is leaf-role only and a zero-
                // bbox leaf is just AX noise (empty hidden Hyperlinks
                // Chromium emits in bulk). Including them inflates the
                // candidate set ~4× on real Arc pages and makes
                // CollectUnderlineCandidatesV's `seen` counter useless.
                if (bb.Width <= 0 || bb.Height <= 0) continue;
                var inter = bb.Intersect(expanded);
                if (inter.Width <= 0 || inter.Height <= 0) continue;
                yield return n;
                yielded++;
            }
        }
    }

    // Rect-pruned descent. Skip subtrees whose own bbox can't possibly contain
    // a leaf intersecting `query` (expanded by `slack`). Empty-bbox nodes
    // (Chromium wrappers, some toolkit AX containers) recurse anyway. Yields
    // (node, bbox) tuples so callers can reuse the bbox we already paid an
    // IPC for in the prune check — saves ~one extra sync read per emitted
    // node in the body.
    private static IEnumerable<(IVisualElement Node, Rect Bbox)> DescendantsInRect(
        IVisualElement root, Rect query, double slack = 8.0)
    {
        var expanded = new Rect(
            query.X - slack, query.Y - slack,
            query.Width + 2 * slack, query.Height + 2 * slack);
        Rect rootBb;
        try { rootBb = ToRect(root.BoundingRectangle); }
        catch { rootBb = default; }
        return DescendantsInRectImpl(root, rootBb, expanded);
    }

    private static IEnumerable<(IVisualElement Node, Rect Bbox)> DescendantsInRectImpl(
        IVisualElement node, Rect nodeBb, Rect expanded)
    {
        yield return (node, nodeBb);
        // Don't recurse into a leaf-role element. Chromium/macOS AX expose
        // multi-line Labels as a single leaf, but their children are
        // per-character or per-glyph wrappers (thousands of nodes, each
        // with bbox INSIDE the parent's bbox so prune doesn't help). The
        // caller already considers `node` itself as a candidate; walking
        // into its glyphs would just re-find the same leaf at every level
        // of detail and inflate the visit count to many thousands per
        // point lookup.
        if (LeafTextRoles.Contains(node.Type)
            || LeafTextOrImageRoles.Contains(node.Type))
            yield break;
        foreach (var c in node.Children)
        {
            Rect cBb;
            try { cBb = ToRect(c.BoundingRectangle); }
            catch { cBb = default; }
            // Prune purely on the child's own bbox: if it has real bounds
            // and they don't intersect the query, the entire subtree is
            // irrelevant. The parent's bbox is NOT consulted — Chromium
            // and other toolkit webviews emit chains of zero-bbox wrapper
            // nodes (Document → Body → Div → ...) before the pixel-bearing
            // leaves; making prune conditional on parent.pruneable means
            // those chains disable pruning all the way down and we walk
            // the entire tree.
            if (cBb.Width > 0 && cBb.Height > 0)
            {
                var inter = cBb.Intersect(expanded);
                if (inter.Width <= 0 || inter.Height <= 0) continue;
            }
            foreach (var d in DescendantsInRectImpl(c, cBb, expanded))
                yield return d;
        }
    }

    private static string F(Rect r) =>
        string.Format(CultureInfo.InvariantCulture, "({0:F0},{1:F0},{2:F0}x{3:F0})",
            r.X, r.Y, r.Width, r.Height);
    private static string F(double x, double y) =>
        string.Format(CultureInfo.InvariantCulture, "({0:F0},{1:F0})", x, y);
    private static string Trunc(string? s, int n = 30)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = s.Replace('\n', ' ').Replace('\r', ' ');
        return t.Length > n ? t[..n] + "…" : t;
    }
}
