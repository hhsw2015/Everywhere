using System.Text;
using Everywhere.Interop;

namespace Everywhere.Mcp.Snapshot;

/// <summary>
/// Renders the indexed tree into the indented text shape expected by upstream
/// <c>get_app_state</c> consumers. Format:
/// <code>
/// [42] Button "Submit" (bounds=0,0,100,30) [enabled, focused]
///   [43] Image (bounds=10,5,16,16)
/// </code>
/// Indentation is two spaces per depth level; per-node text is truncated at
/// <see cref="UpstreamConstants.SnapshotTextDefaultCharacterLimit"/> chars unless
/// <c>showFullText=true</c>.
/// </summary>
public static class SnapshotRenderer
{
    public static string Render(IReadOnlyList<ElementIndexer.IndexedNode> nodes, bool showFullText)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        // Build childrenByParent so shouldElideNode can peek.
        var byParent = new Dictionary<int, List<ElementIndexer.IndexedNode>>();
        foreach (var n in nodes)
        {
            if (!byParent.TryGetValue(n.ParentIndex, out var list))
                byParent[n.ParentIndex] = list = new List<ElementIndexer.IndexedNode>();
            list.Add(n);
        }

        var limit = showFullText ? -1 : UpstreamConstants.SnapshotTextDefaultCharacterLimit;
        var sb = new StringBuilder();
        foreach (var node in nodes)
        {
            // OCCU shouldElideNode: skip rendering for empty AXGroup/Panel
            // wrappers that contribute no information — they only add
            // tree noise. We still keep them in the indexed map (for
            // parent-id lookup) but they won't appear in tree_text.
            if (ShouldElide(node, byParent)) continue;

            sb.Append(' ', node.Depth * 2);
            sb.Append('[').Append(node.Index).Append("] ");
            sb.Append(DisplayRole(node.Element));

            var name = SnapshotTextUtil.Sanitize(node.Element.Name, limit);
            if (!string.IsNullOrEmpty(name))
            {
                sb.Append(' ').Append('"').Append(EscapeQuotes(name)).Append('"');
            }

            var text = SnapshotTextUtil.Sanitize(node.Element.GetText(maxLength: limit), limit);
            if (!string.IsNullOrEmpty(text) && text != name)
            {
                sb.Append(" text=\"").Append(EscapeQuotes(text)).Append('"');
            }

            var bounds = node.Element.BoundingRectangle;
            sb.Append(" (bounds=").Append(bounds.X).Append(',').Append(bounds.Y)
              .Append(',').Append(bounds.Width).Append(',').Append(bounds.Height).Append(')');

            var states = node.Element.States;
            if (states != VisualElementStates.None)
            {
                sb.Append(" [").Append(states.ToString().Replace(", ", ",")).Append(']');
            }

            // OCCU meaningfulActions display: when an element advertises
            // verbs the agent doesn't get from the type alone (slider
            // Increment, popover ShowMenu), surface them inline.
            try
            {
                var actions = node.Element.SupportedActions;
                if (actions is { Count: > 0 })
                {
                    sb.Append(" actions=[");
                    for (var i = 0; i < actions.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        var a = actions[i];
                        if (a.StartsWith("AX", StringComparison.Ordinal)) a = a[2..];
                        sb.Append(a);
                    }
                    sb.Append(']');
                }
            }
            catch { /* best-effort */ }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>OCCU displayRoleText: human-friendly role label.</summary>
    private static string DisplayRole(IVisualElement el)
    {
        // Prefer typed VisualElementType which already applies our
        // platform-portable mapping; fall back to AXRoleDescription
        // shape if the type maps to Unknown.
        return el.Type.ToString();
    }

    /// <summary>
    /// OCCU shouldElideNode: empty AXGroup / Panel containers with a
    /// single child contribute no useful info — eliding them collapses
    /// "Group > Group > Group > Button" chains into just "Button".
    /// </summary>
    private static bool ShouldElide(
        ElementIndexer.IndexedNode node,
        Dictionary<int, List<ElementIndexer.IndexedNode>> byParent)
    {
        if (node.Depth == 0) return false; // never elide root
        if (node.Element.Type != VisualElementType.Panel
            && node.Element.Type != VisualElementType.Unknown) return false;
        var name = node.Element.Name;
        if (!string.IsNullOrEmpty(name)) return false;
        var states = node.Element.States;
        if (states != VisualElementStates.None) return false;
        if (!byParent.TryGetValue(node.Index, out var children)) return false;
        return children.Count >= 1; // empty named group with kids → noise
    }

    private static string EscapeQuotes(string s) =>
        s.Replace("\\", "\\\\")
         .Replace("\"", "\\\"")
         .Replace("\r", "\\r")
         .Replace("\n", "\\n")
         .Replace("\t", "\\t");
}
