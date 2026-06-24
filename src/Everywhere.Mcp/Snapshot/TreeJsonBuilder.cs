using Everywhere.Mcp.Tools.Schemas;
using Everywhere.Interop;

namespace Everywhere.Mcp.Snapshot;

/// <summary>
/// Builds a structured JSON tree from BFS-walked nodes. Parent linkage comes from
/// <see cref="ElementIndexer.IndexedNode.ParentIndex"/> recorded during the walk, which
/// is robust against platform wrappers that produce a NEW <see cref="IVisualElement"/>
/// instance every time you ask for a child or a parent.
/// </summary>
public static class TreeJsonBuilder
{
    public static TreeNode? Build(IReadOnlyList<ElementIndexer.IndexedNode> nodes)
    {
        if (nodes.Count == 0) return null;

        var byIndex = new Dictionary<int, TreeNode>(nodes.Count);
        TreeNode? rootNode = null;

        foreach (var indexed in nodes)
        {
            var bounds = indexed.Element.BoundingRectangle;
            var node = new TreeNode
            {
                ElementIndex = indexed.Index,
                Type = indexed.Element.Type.ToString(),
                Name = indexed.Element.Name,
                Text = indexed.Element.GetText(maxLength: UpstreamConstants.SnapshotTextDefaultCharacterLimit),
                Bounds = new WindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height),
                States = indexed.Element.States == VisualElementStates.None ? null : indexed.Element.States.ToString(),
                Actions = ResolveActions(indexed.Element),
            };
            byIndex[indexed.Index] = node;

            if (indexed.ParentIndex < 0)
            {
                rootNode = node;
                continue;
            }

            if (byIndex.TryGetValue(indexed.ParentIndex, out var parent))
            {
                parent.Children ??= [];
                parent.Children.Add(node);
            }
        }

        return rootNode;
    }

    /// <summary>
    /// OCCU meaningfulActions: only emit the actions list when there are
    /// actual verbs to expose; null otherwise so JSON stays compact.
    /// </summary>
    private static List<string>? ResolveActions(IVisualElement element)
    {
        try
        {
            var src = element.SupportedActions;
            if (src is null || src.Count == 0) return null;
            var stripped = new List<string>(src.Count);
            foreach (var a in src)
            {
                // Strip the "AX" prefix for human-friendly output: agents
                // see [Press, Open, ShowMenu] instead of [AXPress, ...].
                stripped.Add(a.StartsWith("AX", StringComparison.Ordinal) ? a[2..] : a);
            }
            return stripped.Count == 0 ? null : stripped;
        }
        catch { return null; }
    }
}
