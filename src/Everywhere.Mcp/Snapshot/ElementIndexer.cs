using Everywhere.Interop;

namespace Everywhere.Mcp.Snapshot;

/// <summary>
/// Walks a visual tree breadth-first and assigns each visited element a stable
/// integer index (<c>"0"</c>, <c>"1"</c>, …) matching upstream wire format. Honors the
/// <see cref="UpstreamConstants.AccessibilityTreeMaxNodeCount"/> /
/// <see cref="UpstreamConstants.AccessibilityTreeMaxDepth"/> caps so tools never emit a
/// billion-node JSON blob into the model context.
/// </summary>
public static class ElementIndexer
{
    public readonly record struct IndexedNode(int Index, int Depth, IVisualElement Element);

    public static IReadOnlyList<IndexedNode> Walk(
        IVisualElement root,
        int maxNodeCount = UpstreamConstants.AccessibilityTreeMaxNodeCount,
        int maxDepth = UpstreamConstants.AccessibilityTreeMaxDepth)
    {
        ArgumentNullException.ThrowIfNull(root);

        var ordered = new List<IndexedNode>(capacity: Math.Min(maxNodeCount, 256));
        var queue = new Queue<(IVisualElement element, int depth)>();
        queue.Enqueue((root, 0));

        var nextIndex = 0;
        while (queue.Count > 0 && ordered.Count < maxNodeCount)
        {
            var (element, depth) = queue.Dequeue();
            ordered.Add(new IndexedNode(nextIndex++, depth, element));

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
                queue.Enqueue((child, depth + 1));
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
