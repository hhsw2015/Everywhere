using Everywhere.Interop;

namespace Everywhere.Mcp.Snapshot;

/// <summary>
/// Walks a visual tree breadth-first and assigns each visited element a stable
/// integer index (<c>"0"</c>, <c>"1"</c>, …) matching upstream wire format. Honors the
/// <see cref="UpstreamConstants.AccessibilityTreeMaxNodeCount"/> /
/// <see cref="UpstreamConstants.AccessibilityTreeMaxDepth"/> caps so tools never emit a
/// billion-node JSON blob into the model context. Records each node's parent index so
/// downstream consumers (TreeJsonBuilder) don't have to rely on object identity, which
/// breaks for platform wrappers that hand out fresh instances per query.
///
/// Inspired by OCCU AccessibilitySnapshot.swift hardening:
///   - Stale-ref recovery: AX queries on a dead/refreshed window throw
///     COM/ObjC exceptions per node. We catch per-node and skip rather
///     than abort the whole walk, so a single bad child doesn't waste the
///     entire snapshot.
///   - Descendant-scan rule: if a hit-record's frame balloons relative to
///     the original (12× area, or 4× height + 2× width over a threshold),
///     it's almost certainly a giant container that doesn't help. Skip
///     descending so we don't blow the node budget on irrelevant content.
///   - Cycle guard: AX wrappers occasionally hand out Parent=self loops
///     on partially-detached views. Tracking visited elements via id keeps
///     us from walking the same subtree forever.
/// </summary>
public static class ElementIndexer
{
    /// <summary>
    /// One BFS-visited node. <paramref name="WebAreaDepth"/> mirrors
    /// OCCU AccessibilitySnapshot.swift L641/698/814 — null outside any
    /// AXWebArea, otherwise the count of AXWebArea ancestors (1 for
    /// nodes directly under one). Lets the renderer apply
    /// shouldPreserveWebAreaGenericContainer rules without re-walking.
    /// </summary>
    public readonly record struct IndexedNode(
        int Index, int ParentIndex, int Depth, IVisualElement Element, int? WebAreaDepth = null);

    public static IReadOnlyList<IndexedNode> Walk(
        IVisualElement root,
        int maxNodeCount = UpstreamConstants.AccessibilityTreeMaxNodeCount,
        int maxDepth = UpstreamConstants.AccessibilityTreeMaxDepth,
        TimeSpan? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(root);

        // Hard wall-clock budget. Without it, large AX trees (Notes,
        // Finder ~500-1000+ nodes × 2 round-trips per node) make the
        // tool hang for 20-60s, indistinguishable from a deadlock to
        // the user. We'd rather return a partial tree at the deadline
        // than make the user kill the call.
        var deadlineSpan = deadline ?? TimeSpan.FromSeconds(5);
        var stopAt = Environment.TickCount64 + (long)deadlineSpan.TotalMilliseconds;

        var ordered = new List<IndexedNode>(capacity: Math.Min(maxNodeCount, 256));
        // Each queue entry carries the *parent's* frame (not the walk
        // root) so the OCCU-style "this subtree is suspiciously huge
        // relative to where we are now" check compares like-with-like.
        // Using the root made the heuristic dead code for full-window
        // walks and over-aggressive for picked-element walks.
        var queue = new Queue<(IVisualElement element, int depth, int parentIndex, Avalonia.PixelRect? parentFrame, int? webAreaDepth)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue((root, 0, -1, null, null));

        var nextIndex = 0;
        // OCR review: log when budget actually fires so a hang is
        // distinguishable from "tree was actually small".
        while (queue.Count > 0 && ordered.Count < maxNodeCount && Environment.TickCount64 < stopAt)
        {
            var (element, depth, parentIndex, parentFrame, parentWebAreaDepth) = queue.Dequeue();

            // Cycle guard. Some platform AX wrappers (Mac AX with detached
            // popovers, Win UIA with virtualized rows) hand out Parent
            // pointers that loop back into the subtree we're already in.
            // Identity-based dedup is unreliable (fresh wrappers per
            // query) so we use Id. Earlier I added a length floor to
            // filter the bounds-derived Windows fallback, but the actual
            // fallback string is "h.x.y.w.h" form (≥11 chars), and
            // legitimate short ids on Linux (hex GetHashCode) and
            // Windows UIA RuntimeId (e.g. "2A.7F") would have been
            // mis-filtered. Use any non-empty id as the dedup key; the
            // collision-prone shape should be filtered at the producer.
            var id = TryGetId(element);
            if (!string.IsNullOrEmpty(id) && !seen.Add(id)) continue;

            var idx = nextIndex++;
            // OCCU webAreaDepth (L641/698/814): null until we cross an
            // AXWebArea, then increments per generation. The element's
            // OWN role being WebArea seeds depth=0; any descendant of
            // a WebArea bumps from parent's value.
            int? webAreaDepth = null;
            try
            {
                var isWebArea = element.Type == VisualElementType.Document; // VisualElementType.Document == AXWebArea
                if (isWebArea) webAreaDepth = 0;
                else if (parentWebAreaDepth is int pwd) webAreaDepth = pwd + 1;
            }
            catch { /* element.Type may throw on stale ref */ }
            ordered.Add(new IndexedNode(idx, parentIndex, depth, element, webAreaDepth));

            if (depth + 1 > maxDepth) continue;

            // ponytail: ShouldScanDescendants needed BoundingRectangle
            // (Position + Size = 2 cross-process round-trips per node).
            // On Notes-class apps with ~500-1000 nodes that alone cost
            // 6-10s of Walk time. The check only filtered "scroller
            // larger than parent" cases — render-time elide handles
            // the same noise without paying the up-front cost.
            // Track parentFrame for Render but don't fetch ownFrame
            // during walk; ownFrame is fetched lazily in Render where
            // it's cached via AXValue caching (v0.9.108+).
            var ownFrame = (Avalonia.PixelRect?)null;

            // Per-child try/catch: a stale AX ref on one child must not
            // abort the entire snapshot walk.
            IEnumerator<IVisualElement>? enumerator = null;
            try { enumerator = element.Children.GetEnumerator(); }
            catch { continue; }

            try
            {
                while (true)
                {
                    IVisualElement? child = null;
                    try
                    {
                        if (!enumerator.MoveNext()) break;
                        child = enumerator.Current;
                    }
                    catch
                    {
                        // Stale ref mid-iteration. Stop this parent's
                        // child enumeration but keep walking the rest of
                        // the queue.
                        break;
                    }
                    if (child is null) continue;
                    if (ordered.Count + queue.Count >= maxNodeCount) break;
                    queue.Enqueue((child, depth + 1, idx, ownFrame, webAreaDepth));
                }
            }
            finally
            {
                try { enumerator.Dispose(); }
                catch { /* stale AX ref on dispose; ignore — same family as the per-child catch above */ }
            }
        }

        if (Environment.TickCount64 >= stopAt && queue.Count > 0)
        {
            // Surface the truncation so the user / caller can distinguish
            // "walked the whole tree (it was small)" from "tree larger
            // than budget allowed; partial tree returned".
            System.Diagnostics.Debug.WriteLine(
                $"[everywhere] ElementIndexer.Walk hit {(int)deadlineSpan.TotalSeconds}s deadline at {ordered.Count} nodes; {queue.Count} nodes left in queue.");
        }

        return ordered;
    }

    public static Dictionary<int, IVisualElement> ToIndexMap(IEnumerable<IndexedNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var map = new Dictionary<int, IVisualElement>();
        foreach (var node in nodes)
        {
            map[node.Index] = node.Element;
        }
        return map;
    }

    private static string? TryGetId(IVisualElement el)
    {
        try { return el.Id; } catch { return null; }
    }

    private static Avalonia.PixelRect? SafeBounds(IVisualElement el)
    {
        try
        {
            var r = el.BoundingRectangle;
            if (r.Width <= 0 || r.Height <= 0) return null;
            return r;
        }
        catch { return null; }
    }

    /// <summary>
    /// Mirrors OCCU shouldScanDescendantsOfHitRecord. If the hit frame
    /// engulfs the original by &gt;12× area or far exceeds it in both
    /// dimensions, the subtree is not on the agent's path of interest —
    /// skip descending to keep the snapshot focused.
    /// </summary>
    private static bool ShouldScanDescendants(Avalonia.PixelRect originalFrame, Avalonia.PixelRect hitFrame)
    {
        var originalArea = Math.Max((double)originalFrame.Width * originalFrame.Height, 1.0);
        var hitArea = (double)hitFrame.Width * hitFrame.Height;
        if (hitArea > Math.Max(originalArea * 12, 20_000)) return false;
        if (hitFrame.Height > Math.Max(originalFrame.Height * 4, 96)
            && hitFrame.Width > Math.Max(originalFrame.Width * 2, 240)) return false;
        return true;
    }
}
