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
            // ponytail: each AX property is a CrossProcess IPC call —
            // Name/States/SupportedActions etc are NOT cached on the
            // element. ShouldElide() then re-asks Name/Text/States/Actions,
            // doubling every IPC. Fetch each one ONCE here, reuse for
            // both elide check and render. On a 60-node tree this cuts
            // ~480 IPC calls to ~240 — get_app_state drops from a slow
            // multi-second roundtrip to something interactive.
            var el = node.Element;
            var name = el.Name;
            var text = el.GetText(maxLength: limit < 0 ? -1 : limit);
            var states = el.States;
            IReadOnlyList<string>? rawActions = null;
            try { rawActions = el.SupportedActions; }
            catch (System.Runtime.InteropServices.COMException) { }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            var filteredActions = SnapshotActionFilter.Filter(rawActions);

            // OCCU shouldElideNode: skip rendering for empty AXGroup/Panel
            // wrappers that contribute no information — they only add
            // tree noise. We still keep them in the indexed map (for
            // parent-id lookup) but they won't appear in tree_text.
            if (ShouldElide(node, byParent, name, text, states, filteredActions)) continue;

            sb.Append(' ', node.Depth * 2);
            sb.Append('[').Append(node.Index).Append("] ");
            sb.Append(DisplayRole(node.Element));

            name = SnapshotTextUtil.Sanitize(name, limit);
            // OCCU summaryMarkdownLinkText (AccessibilitySnapshot.swift
            // L1770-1789): AXLink rows render as "[label](url)" so the
            // agent can copy/follow the URL straight out of the
            // snapshot. Brackets in label are markdown-escaped.
            if (node.Element.Type == VisualElementType.Hyperlink
                && !string.IsNullOrEmpty(name)
                && !string.IsNullOrEmpty(node.Element.Url))
            {
                var safe = name.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]");
                sb.Append(" \"[").Append(safe).Append("](").Append(node.Element.Url).Append(")\"");
            }
            else if (!string.IsNullOrEmpty(name))
            {
                sb.Append(' ').Append('"').Append(EscapeQuotes(name)).Append('"');
            }

            text = SnapshotTextUtil.Sanitize(text, limit);
            if (!string.IsNullOrEmpty(text) && text != name)
            {
                sb.Append(" text=\"").Append(EscapeQuotes(text)).Append('"');
            }

            // OCCU formattedLabelSegment (AX L1212): "Description: ..."
            // when AXDescription differs from title. Skipped when label
            // duplicates Name (already shown above).
            var desc = SnapshotTextUtil.Sanitize(node.Element.AccessibleDescription, limit);
            if (!string.IsNullOrEmpty(desc) && desc != name && desc != text)
            {
                sb.Append(" desc=\"").Append(EscapeQuotes(desc)).Append('"');
            }
            // OCCU formattedPlaceholderSegment (AX L1243).
            var ph = SnapshotTextUtil.Sanitize(node.Element.Placeholder, limit);
            if (!string.IsNullOrEmpty(ph) && ph != name && ph != text && ph != desc)
            {
                sb.Append(" placeholder=\"").Append(EscapeQuotes(ph)).Append('"');
            }

            var bounds = node.Element.BoundingRectangle;
            sb.Append(" (bounds=").Append(bounds.X).Append(',').Append(bounds.Y)
              .Append(',').Append(bounds.Width).Append(',').Append(bounds.Height).Append(')');

            if (states != VisualElementStates.None)
            {
                sb.Append(" [").Append(states.ToString().Replace(", ", ",")).Append(']');
            }

            // OCCU meaningfulActions display: when an element advertises
            // verbs the agent doesn't get from the type alone (slider
            // Increment, popover ShowMenu), surface them inline. Same
            // whitelist/dedup as TreeJsonBuilder via SnapshotActionFilter.
            // filteredActions was computed once at the top of the loop.
            if (filteredActions.Count > 0)
            {
                sb.Append(" actions=[");
                for (var i = 0; i < filteredActions.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(filteredActions[i]);
                }
                sb.Append(']');
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>OCCU displayRoleText (AccessibilitySnapshot.swift L1552):
    /// remap a few specific shapes to nicer labels — radio group with
    /// a label collapses to "" (the label is enough), AXLink → "link",
    /// AXWebArea → "HTML 内容", suppressed children → "container",
    /// AXMenuBarItem returns "" (just a wrapper). Otherwise lowercased
    /// type name.
    /// </summary>
    private static string DisplayRole(IVisualElement el)
    {
        var type = el.Type;
        var name = el.Name;
        return type switch
        {
            VisualElementType.Hyperlink => "link",
            VisualElementType.Document => "HTML 内容",
            VisualElementType.RadioButton when !string.IsNullOrEmpty(name) => "",
            _ => type.ToString(),
        };
    }

    /// <summary>
    /// Delegates to SnapshotElideRules (line-by-line port of OCCU
    /// shouldElideNode + shouldPreserveWebAreaGenericContainer).
    /// </summary>
    private static bool ShouldElide(
        ElementIndexer.IndexedNode node,
        Dictionary<int, List<ElementIndexer.IndexedNode>> byParent,
        string? cachedName,
        string? cachedText,
        VisualElementStates cachedStates,
        IReadOnlyList<string> cachedFilteredActions)
    {
        if (node.Depth == 0) return false; // never elide root
        var children = byParent.TryGetValue(node.Index, out var list) ? list : null;
        var childCount = children?.Count ?? 0;
        return SnapshotElideRules.ShouldElide(
            type: node.Element.Type,
            name: cachedName,
            text: cachedText,
            states: cachedStates,
            actions: cachedFilteredActions,
            childCount: childCount,
            webAreaDepth: node.WebAreaDepth);
    }

    private static string EscapeQuotes(string s) =>
        s.Replace("\\", "\\\\")
         .Replace("\"", "\\\"")
         .Replace("\r", "\\r")
         .Replace("\n", "\\n")
         .Replace("\t", "\\t");
}
