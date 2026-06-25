using System.ComponentModel;
using System.Text.Json;
using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using Everywhere.Mcp.Tools.Schemas;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class ReadPickTool
{
    [McpServerTool(Name = "read_pick", ReadOnly = true)]
    [Description(
        "Read the UI element the user explicitly pinned for this agent via Everywhere's " +
        "configurable Pin-Element hotkey. Returns {\"pinned\": true, \"picked_index\": int, " +
        "\"app\": str, \"element\": {...}} — the picked element is at picked_index inside the " +
        "indexed tree, addressable by click/set_value/scroll. Returns {\"pinned\": false} when " +
        "no pin is fresh (the pin is one-shot: reading consumes it; pins expire after 5 min). " +
        "ALWAYS call this FIRST when the user uses deictic references (\"this\", \"that\", " +
        "\"the button I just selected\", \"刚才那个\") — fall back to get_app_context or " +
        "get_focused_context when pinned is false. " +
        "PARAM mode: \"auto\" (default — picks links view when the pin is a list of " +
        "hyperlinks, otherwise full tree), \"links\" (force url+label pairs only — best " +
        "when pin is a popup of urls), \"text\" (force plain text aggregation), \"full\" " +
        "(force the verbose tree). \"links\" / \"text\" cut output ~10× vs \"full\".")]
    public static CallToolResult ReadPick(
        PickStash stash,
        SessionStore sessions,
        [Description("Output mode: auto | links | text | full. Default auto.")] string? mode = null,
        bool include_tree_json = false)
    {
        try
        {
            var picked = stash.Take();
            if (picked is null)
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock
                    {
                        Text = "{\"pinned\":false,\"picked_index\":null,\"app\":null,\"element\":null}",
                    }],
                };
            }

            var nodes = ElementIndexer.Walk(picked);
            var elementMap = ElementIndexer.ToIndexMap(nodes);
            var appKey = AppKey.FromProcessId(picked.ProcessId);
            sessions.Issue(appKey, elementMap, picked.NativeWindowHandle);

            var bounds = picked.BoundingRectangle;
            var resolvedMode = ResolveMode(mode, nodes);

            object element;
            if (resolvedMode is "links" or "text")
            {
                // Render to markdown directly (skill-style), not nested JSON.
                // Saves another ~50% over a json wrapper since the agent
                // would otherwise re-format anyway. Same shape as xlb-skill's
                // browse output — agent treats it interchangeably.
                var markdown = resolvedMode == "links"
                    ? RenderLinksMarkdown(nodes)
                    : RenderTextMarkdown(nodes);
                element = new
                {
                    app = appKey,
                    window_title = picked.Name,
                    mode = resolvedMode,
                    markdown,
                };
            }
            else
            {
                var payload = new FocusedContextResult
                {
                    App = appKey,
                    WindowTitle = picked.Name,
                    WindowBounds = new WindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height),
                    FocusedSummary = picked.GetText(maxLength: UpstreamConstants.SnapshotTextDefaultCharacterLimit),
                    TreeText = SnapshotRenderer.Render(nodes, showFullText: false),
                    // ponytail: TreeJson duplicates TreeText. Default omit, opt in per call.
                    TreeJson = include_tree_json ? TreeJsonBuilder.Build(nodes) : null,
                };
                SemanticEnricher.Apply(payload, nodes);
                element = payload;
            }

            var json = JsonSerializer.Serialize(new
            {
                pinned = true,
                picked_index = nodes.Count > 0 ? nodes[0].Index : 0,
                app = appKey,
                mode = resolvedMode,
                element,
            });
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = json }],
            };
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "read_pick");
        }
    }

    /// <summary>
    /// Decide between full / links / text. "auto" picks "links" only when the
    /// pinned subtree is dominated by hyperlink children (typical xlinkBook
    /// popup, clipboard list of url collections). Falls back to full for
    /// arbitrary UI elements where layout / ancestor tree is meaningful.
    /// </summary>
    private static string ResolveMode(string? requested, IReadOnlyList<ElementIndexer.IndexedNode> nodes)
    {
        var m = (requested ?? string.Empty).Trim().ToLowerInvariant();
        if (m is "links" or "text" or "full") return m;
        // auto
        if (nodes.Count == 0) return "full";
        var hyperlinkCount = 0;
        var totalCount = 0;
        foreach (var n in nodes)
        {
            totalCount++;
            if (n.Element.Type == VisualElementType.Hyperlink) hyperlinkCount++;
        }
        // List-of-links heuristic: ≥3 hyperlinks AND ≥30% of nodes are hyperlinks
        if (hyperlinkCount >= 3 && hyperlinkCount * 100 >= totalCount * 30) return "links";
        return "full";
    }

    /// <summary>
    /// Render the pinned subtree's hyperlinks as a flat markdown bullet list.
    /// One link per line: <c>- &lt;url-or-label&gt;</c>. Skips icon-only links
    /// (label too short to be meaningful) and de-duplicates exact repeats.
    /// Output mirrors the shape skill scripts (xlb_local_reader browse) emit
    /// so agents can treat both pipelines interchangeably.
    /// </summary>
    private static string RenderLinksMarkdown(IReadOnlyList<ElementIndexer.IndexedNode> nodes)
    {
        var sb = new System.Text.StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in nodes)
        {
            if (n.Element.Type != VisualElementType.Hyperlink) continue;
            var label = (n.Element.GetText(maxLength: 200) ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(label))
            {
                foreach (var child in nodes)
                {
                    if (child.ParentIndex != n.Index) continue;
                    var ct = child.Element.GetText(maxLength: 200);
                    if (!string.IsNullOrWhiteSpace(ct)) { label = ct.Trim(); break; }
                }
            }
            if (label.Length < 2) continue;
            if (!seen.Add(label)) continue;
            sb.Append("- ").Append(label).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Render the pinned subtree as a plain text paragraph (one Label per
    /// line, blank-line separated when consecutive labels start far apart).
    /// </summary>
    private static string RenderTextMarkdown(IReadOnlyList<ElementIndexer.IndexedNode> nodes)
    {
        var sb = new System.Text.StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in nodes)
        {
            if (n.Element.Type != VisualElementType.Label) continue;
            var t = n.Element.GetText(maxLength: 500);
            if (string.IsNullOrWhiteSpace(t)) continue;
            t = t.Trim();
            if (!seen.Add(t)) continue;
            sb.Append(t).Append('\n');
        }
        return sb.ToString();
    }
}
