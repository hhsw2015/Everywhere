using Everywhere.Interop;
using Everywhere.Mcp.Tools.Schemas;

namespace Everywhere.Mcp.Snapshot;

/// <summary>
/// Builds a structured JSON tree from a list of <see cref="ElementIndexer.IndexedNode"/>.
/// Returns the same indices used by <see cref="SnapshotRenderer"/>, so agents can switch
/// between the indented text form and the JSON form without re-snapshotting.
/// </summary>
public static class TreeJsonBuilder
{
    public static TreeNode? Build(IReadOnlyList<ElementIndexer.IndexedNode> nodes)
    {
        if (nodes.Count == 0)
        {
            return null;
        }

        var elementToNode = new Dictionary<IVisualElement, TreeNode>(ReferenceEqualityComparer.Instance);
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
            };
            elementToNode[indexed.Element] = node;

            if (rootNode is null)
            {
                rootNode = node;
                continue;
            }

            if (indexed.Element.Parent is { } parent
                && elementToNode.TryGetValue(parent, out var parentNode))
            {
                parentNode.Children ??= [];
                parentNode.Children.Add(node);
            }
        }

        return rootNode;
    }
}
