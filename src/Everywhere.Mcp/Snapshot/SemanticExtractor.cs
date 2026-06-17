using Everywhere.Interop;
using Everywhere.Mcp.Tools.Schemas;

namespace Everywhere.Mcp.Snapshot;

/// <summary>
/// Pulls "semantic" first-class views out of an indexed tree so the agent doesn't
/// have to grep tree_text. Surfaces selection / focus / actions that the platform
/// a11y layer already knows about.
/// </summary>
public static class SemanticExtractor
{
    public static List<SemanticItem> ExtractSelected(IReadOnlyList<ElementIndexer.IndexedNode> nodes)
    {
        var result = new List<SemanticItem>();
        foreach (var n in nodes)
        {
            if ((n.Element.States & VisualElementStates.Selected) != 0)
            {
                result.Add(BuildItem(n));
            }
        }
        return result;
    }

    /// <summary>
    /// Returns ONLY the deepest Focused node (or empty). On macOS / Windows a11y the
    /// Focused flag propagates up the ancestor chain (window → pane → list → row →
    /// cell), so returning every flagged node would dump the whole chain — confusing
    /// for the agent. focused_path already covers the chain in order; here we surface
    /// the actual leaf-level focus.
    /// </summary>
    public static List<SemanticItem> ExtractFocused(IReadOnlyList<ElementIndexer.IndexedNode> nodes)
    {
        ElementIndexer.IndexedNode? deepest = null;
        foreach (var n in nodes)
        {
            if ((n.Element.States & VisualElementStates.Focused) == 0) continue;
            if (deepest is null || n.Depth > deepest.Value.Depth) deepest = n;
        }
        return deepest is null ? [] : [BuildItem(deepest.Value)];
    }

    /// <summary>
    /// Walk down from the root building a path of (element_index, type, name) for
    /// the chain leading to the deepest Focused node — gives agent quick orientation
    /// "you are inside Downloads → TreeView → Panel: README.md".
    /// </summary>
    public static List<SemanticItem> BuildFocusedPath(IReadOnlyList<ElementIndexer.IndexedNode> nodes)
    {
        if (nodes.Count == 0) return [];

        var byIndex = nodes.ToDictionary(n => n.Index);
        var deepestFocused = nodes
            .Where(n => (n.Element.States & VisualElementStates.Focused) != 0)
            .OrderByDescending(n => n.Depth)
            .FirstOrDefault();
        if (deepestFocused.Element is null) return [];

        var path = new List<SemanticItem>();
        var cur = deepestFocused;
        while (true)
        {
            path.Add(BuildItem(cur));
            if (cur.ParentIndex < 0 || !byIndex.TryGetValue(cur.ParentIndex, out var parent)) break;
            cur = parent;
        }
        path.Reverse();
        return path;
    }

    private static SemanticItem BuildItem(ElementIndexer.IndexedNode node)
    {
        // Inline label text: prefer the element's own name/text, fall back to first
        // labeled descendant so Finder TableRow → file-name shows up.
        var ownText = (node.Element.Name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(ownText))
        {
            ownText = (node.Element.GetText(maxLength: 200) ?? string.Empty).Trim();
        }
        if (string.IsNullOrEmpty(ownText))
        {
            ownText = FindLabelTextInChildren(node.Element, maxDepth: 3) ?? string.Empty;
        }

        return new SemanticItem
        {
            ElementIndex = node.Index,
            Type = node.Element.Type.ToString(),
            Text = string.IsNullOrEmpty(ownText) ? null : ownText,
            States = StatesToList(node.Element.States),
            AvailableActions = SuggestActions(node.Element.Type),
        };
    }

    private static List<string>? StatesToList(VisualElementStates states)
    {
        if (states == VisualElementStates.None) return null;
        var list = new List<string>();
        foreach (VisualElementStates flag in Enum.GetValues<VisualElementStates>())
        {
            if (flag == VisualElementStates.None) continue;
            if ((states & flag) == flag)
            {
                list.Add(flag.ToString());
            }
        }
        return list.Count == 0 ? null : list;
    }

    private static List<string>? SuggestActions(VisualElementType type) => type switch
    {
        VisualElementType.Button or VisualElementType.Hyperlink or VisualElementType.MenuItem
            or VisualElementType.HeaderItem or VisualElementType.TabItem
            or VisualElementType.RadioButton or VisualElementType.CheckBox
            => ["click", "perform_secondary_action"],

        VisualElementType.ListViewItem or VisualElementType.TreeViewItem or VisualElementType.DataGridItem
            => ["click", "perform_secondary_action"],

        VisualElementType.TextEdit
            => ["set_value", "click"],

        VisualElementType.Slider or VisualElementType.Spinner
            => ["set_value"],

        VisualElementType.ComboBox
            => ["click", "set_value"],

        VisualElementType.ListView or VisualElementType.TreeView or VisualElementType.DataGrid
            or VisualElementType.Document
            => ["scroll", "expand_element"],

        VisualElementType.Image
            => ["click"],

        _ => null,
    };

    private static string? FindLabelTextInChildren(IVisualElement element, int maxDepth)
    {
        if (maxDepth <= 0) return null;
        foreach (var child in element.Children)
        {
            if (child.Type == VisualElementType.Label)
            {
                var t = (child.Name ?? child.GetText(maxLength: 200) ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(t)) return t;
            }
            var nested = FindLabelTextInChildren(child, maxDepth - 1);
            if (!string.IsNullOrEmpty(nested)) return nested;
        }
        return null;
    }
}
