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
    public readonly record struct IndexedNode(int Index, int ParentIndex, int Depth, IVisualElement Element);

    public static IReadOnlyList<IndexedNode> Walk(
        IVisualElement root,
        int maxNodeCount = UpstreamConstants.AccessibilityTreeMaxNodeCount,
        int maxDepth = UpstreamConstants.AccessibilityTreeMaxDepth)
    {
        ArgumentNullException.ThrowIfNull(root);

        var ordered = new List<IndexedNode>(capacity: Math.Min(maxNodeCount, 256));
        var queue = new Queue<(IVisualElement element, int depth, int parentIndex, Avalonia.PixelRect? hitFrame)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Avalonia.PixelRect? rootFrame = SafeBounds(root);
        queue.Enqueue((root, 0, -1, rootFrame));

        var nextIndex = 0;
        while (queue.Count > 0 && ordered.Count < maxNodeCount)
        {
            var (element, depth, parentIndex, hitFrame) = queue.Dequeue();

            // Cycle guard. Some platform AX wrappers (Mac AX with detached
            // popovers, Win UIA with virtualized rows) hand out Parent
            // pointers that loop back into the subtree we're already in.
            // Identity-based dedup is unreliable (fresh wrappers per
            // query) so we use Id.
            var id = TryGetId(element);
            if (id is not null && !seen.Add(id)) continue;

            var idx = nextIndex++;
            ordered.Add(new IndexedNode(idx, parentIndex, depth, element));

            if (depth + 1 > maxDepth) continue;

            // OCCU shouldScanDescendantsOfHitRecord: skip descending when
            // this element's frame is dramatically larger than the root
            // (likely a top-level scroller / web area that engulfs the
            // real target).
            if (rootFrame is { } rf && hitFrame is { } hf && !ShouldScanDescendants(rf, hf))
                continue;

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
                    queue.Enqueue((child, depth + 1, idx, SafeBounds(child)));
                }
            }
            finally
            {
                enumerator.Dispose();
            }
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
