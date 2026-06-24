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
                Name = SnapshotTextUtil.Sanitize(indexed.Element.Name) is var n && string.IsNullOrEmpty(n) ? null : n,
                Text = SnapshotTextUtil.Sanitize(indexed.Element.GetText(maxLength: UpstreamConstants.SnapshotTextDefaultCharacterLimit)) is var t && string.IsNullOrEmpty(t) ? null : t,
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
    /// actual UI-state-changing verbs to expose; null otherwise so JSON
    /// stays compact. Whitelist + dedup live in
    /// <see cref="SnapshotActionFilter"/> so JSON and tree_text agree.
    /// </summary>
    private static List<string>? ResolveActions(IVisualElement element)
    {
        IReadOnlyList<string> src;
        try { src = element.SupportedActions; }
        catch (System.Runtime.InteropServices.COMException) { return null; }
        catch (ObjectDisposedException) { return null; }
        catch (InvalidOperationException) { return null; }
        var filtered = SnapshotActionFilter.Filter(src);
        return filtered.Count == 0 ? null : filtered;
    }
}
