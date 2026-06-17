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
        var queue = new Queue<(IVisualElement element, int depth, int parentIndex)>();
        queue.Enqueue((root, 0, -1));

        var nextIndex = 0;
        while (queue.Count > 0 && ordered.Count < maxNodeCount)
        {
            var (element, depth, parentIndex) = queue.Dequeue();
            var idx = nextIndex++;
            ordered.Add(new IndexedNode(idx, parentIndex, depth, element));

            if (depth + 1 > maxDepth)
            {
                continue;
            }

            foreach (var child in element.Children)
            {
                if (ordered.Count + queue.Count >= maxNodeCount)
                {
                    break;
                }
                queue.Enqueue((child, depth + 1, idx));
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
}
