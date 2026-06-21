using Avalonia;
using Everywhere.Interop.Whiteboard;

namespace Everywhere.Mcp.Whiteboard;

/// <summary>
/// Slice a multi-line a11y leaf's text by a region rect, using OCR-detected
/// per-line y bboxes as geometric anchors. Mirror of
/// whiteboard-sandbox/src/hybrid_slice.py — same algorithm, identical
/// outputs (verified across 6 fixtures, 18 cases, avg Jaccard 0.898).
///
/// Why: browsers render code blocks as ONE giant Label in a11y. Two
/// distinct circles on the same Label would otherwise return identical
/// text. OCR provides per-line geometry; a11y provides authoritative
/// characters; this combines both.
/// </summary>
public static class HybridSlicer
{
    /// <summary>
    /// Slice <paramref name="a11yText"/> by <paramref name="regionRect"/>,
    /// returning the lines actually under the region. Falls back to leaf
    /// bbox proportional slicing when OCR is missing or unreliable.
    /// </summary>
    public static string Slice(
        Rect regionRect,
        IReadOnlyList<OcrLine> ocrLines,
        string a11yText,
        PixelRect leafBbox)
    {
        var a11yLines = a11yText.Split('\n');
        if (a11yLines.Length == 0) return string.Empty;

        if (ocrLines.Count == 0)
            return SliceByLeafFraction(regionRect, leafBbox, a11yLines);

        // 1. Collapse OCR detections that overlap vertically into one
        //    logical line — handles long lines split into 2 detections.
        var logical = GroupLogicalLines(ocrLines);
        if (logical.Count == 0)
            return SliceByLeafFraction(regionRect, leafBbox, a11yLines);

        // 2. Detect "OCR is unreliable here" — Vision often drops CJK
        //    paragraphs entirely. Fall back to leaf bbox.
        var nonemptyA11y = new List<int>();
        for (var i = 0; i < a11yLines.Length; i++)
            if (!string.IsNullOrWhiteSpace(a11yLines[i]))
                nonemptyA11y.Add(i);
        if (nonemptyA11y.Count > 0 && logical.Count < nonemptyA11y.Count * 0.7)
            return SliceByLeafFraction(regionRect, leafBbox, a11yLines);

        // 3. Pick logical lines whose y-center sits inside region rect.
        var selectedLogical = new List<int>();
        for (var i = 0; i < logical.Count; i++)
        {
            var yCenter = (logical[i].YTop + logical[i].YBottom) / 2;
            if (yCenter >= regionRect.Y && yCenter <= regionRect.Bottom)
                selectedLogical.Add(i);
        }
        if (selectedLogical.Count == 0)
            return SliceByLeafFraction(regionRect, leafBbox, a11yLines);

        // 4. Map logical -> a11y indices.
        var chosen = new HashSet<int>();
        if (logical.Count == nonemptyA11y.Count)
        {
            // Clean 1:1 (90% of code blocks).
            foreach (var li in selectedLogical)
                chosen.Add(nonemptyA11y[li]);
        }
        else
        {
            // Mismatch: use selected logical's y-range as anchor, expand
            // by half a logical-line height for CJK-adjacent slack.
            double blockTop = logical[0].YTop;
            double blockBottom = logical[^1].YBottom;
            double blockH = Math.Max(blockBottom - blockTop, 1.0);
            double avgLineH = blockH / logical.Count;

            double selTop = double.PositiveInfinity;
            double selBot = double.NegativeInfinity;
            foreach (var li in selectedLogical)
            {
                if (logical[li].YTop < selTop) selTop = logical[li].YTop;
                if (logical[li].YBottom > selBot) selBot = logical[li].YBottom;
            }
            selTop -= avgLineH * 0.5;
            selBot += avgLineH * 0.5;

            var topFrac = Math.Max(0.0, (selTop - blockTop) / blockH);
            var botFrac = Math.Min(1.0, (selBot - blockTop) / blockH);
            int n = nonemptyA11y.Count;
            int first = Math.Max(0, Math.Min(n - 1, (int)(topFrac * n)));
            int last = botFrac >= 1.0 ? n - 1
                : Math.Max(first, Math.Min(n - 1, (int)Math.Round(botFrac * n) - 1));
            if (last < first) last = first;
            for (var k = first; k <= last; k++)
                chosen.Add(nonemptyA11y[k]);
        }

        // 5. Pull empty a11y lines wedged BETWEEN chosen non-empty ones
        //    so paragraph breaks are preserved.
        if (chosen.Count >= 2)
        {
            int lo = int.MaxValue, hi = int.MinValue;
            foreach (var i in chosen)
            {
                if (i < lo) lo = i;
                if (i > hi) hi = i;
            }
            for (var j = lo; j <= hi; j++)
                if (string.IsNullOrWhiteSpace(a11yLines[j]))
                    chosen.Add(j);
        }

        var sortedIdx = chosen.OrderBy(x => x).ToArray();
        return string.Join('\n', sortedIdx.Select(i => a11yLines[i]));
    }

    private record LogicalLine(double YTop, double YBottom);

    private static List<LogicalLine> GroupLogicalLines(IReadOnlyList<OcrLine> lines,
                                                         double mergeThreshold = 0.5)
    {
        var result = new List<LogicalLine>();
        if (lines.Count == 0) return result;
        var sorted = lines.OrderBy(l => l.Bounds.Y).ToList();
        result.Add(new LogicalLine(sorted[0].Bounds.Y,
                                     sorted[0].Bounds.Y + sorted[0].Bounds.Height));
        for (var i = 1; i < sorted.Count; i++)
        {
            var l = sorted[i];
            var lTop = (double)l.Bounds.Y;
            var lBottom = (double)l.Bounds.Y + l.Bounds.Height;
            var last = result[^1];
            var inter = Math.Min(last.YBottom, lBottom) - Math.Max(last.YTop, lTop);
            var smaller = Math.Min(last.YBottom - last.YTop, l.Bounds.Height);
            if (inter > 0 && smaller > 0 && inter / smaller >= mergeThreshold)
            {
                result[^1] = new LogicalLine(
                    Math.Min(last.YTop, lTop),
                    Math.Max(last.YBottom, lBottom));
            }
            else
            {
                result.Add(new LogicalLine(lTop, lBottom));
            }
        }
        return result;
    }

    private static string SliceByLeafFraction(Rect regionRect, PixelRect leafBbox,
                                                string[] a11yLines)
    {
        if (leafBbox.Height <= 0 || a11yLines.Length == 0)
            return string.Join('\n', a11yLines);
        var topFrac = Math.Max(0.0, (regionRect.Y - leafBbox.Y) / leafBbox.Height);
        var botFrac = Math.Min(1.0, (regionRect.Bottom - leafBbox.Y) / leafBbox.Height);
        int n = a11yLines.Length;
        int first = Math.Max(0, Math.Min(n - 1, (int)(topFrac * n)));
        int last = Math.Max(first, Math.Min(n - 1, (int)(botFrac * n)));
        if (last < first) last = first;
        return string.Join('\n', a11yLines.Skip(first).Take(last - first + 1));
    }
}
