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

    private static readonly HashSet<string> _meaningfulVerbs = new(StringComparer.Ordinal)
    {
        "Press", "Confirm", "Open", "ShowMenu",
        "Increment", "Decrement", "Pick", "Cancel", "Delete", "Raise",
    };

    /// <summary>
    /// OCCU meaningfulActions: only emit the actions list when there are
    /// actual UI-state-changing verbs to expose; null otherwise so JSON
    /// stays compact. Filters defensively (caller may not be the Mac AX
    /// path that already pre-filters) and skips null/empty entries.
    /// </summary>
    private static List<string>? ResolveActions(IVisualElement element)
    {
        IReadOnlyList<string> src;
        try { src = element.SupportedActions; }
        catch (System.Runtime.InteropServices.COMException) { return null; }
        catch (ObjectDisposedException) { return null; }
        catch (InvalidOperationException) { return null; }
        if (src is null || src.Count == 0) return null;
        var stripped = new List<string>(src.Count);
        foreach (var raw in src)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var a = raw.StartsWith("AX", StringComparison.Ordinal) ? raw[2..] : raw;
            if (string.IsNullOrEmpty(a)) continue;
            if (!_meaningfulVerbs.Contains(a)) continue;
            stripped.Add(a);
        }
        return stripped.Count == 0 ? null : stripped;
    }
}
