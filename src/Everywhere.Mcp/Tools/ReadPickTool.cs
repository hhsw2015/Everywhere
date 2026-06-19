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
        [Description("Output mode: auto | links | text | full. Default auto.")] string? mode = null)
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
                element = new
                {
                    app = appKey,
                    window_title = picked.Name,
                    window_bounds = new WindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height),
                    mode = resolvedMode,
                    items = resolvedMode == "links"
                        ? (object)ExtractLinks(nodes)
                        : ExtractText(nodes),
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
                    TreeJson = TreeJsonBuilder.Build(nodes),
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
            if (n.Element.Type == VisualElementType.HyperLink) hyperlinkCount++;
        }
        // List-of-links heuristic: ≥3 hyperlinks AND ≥30% of nodes are hyperlinks
        if (hyperlinkCount >= 3 && hyperlinkCount * 100 >= totalCount * 30) return "links";
        return "full";
    }

    private static List<LinkItem> ExtractLinks(IReadOnlyList<ElementIndexer.IndexedNode> nodes)
    {
        var items = new List<LinkItem>();
        foreach (var n in nodes)
        {
            if (n.Element.Type != VisualElementType.HyperLink) continue;
            var label = n.Element.GetText(maxLength: 200) ?? string.Empty;
            // anchor labels often live in a child Label rather than the Hyperlink itself.
            if (string.IsNullOrWhiteSpace(label))
            {
                foreach (var child in nodes)
                {
                    if (child.ParentIndex != n.Index) continue;
                    var ct = child.Element.GetText(maxLength: 200);
                    if (!string.IsNullOrWhiteSpace(ct)) { label = ct; break; }
                }
            }
            label = label.Trim();
            // skip icon-only / single-character control buttons
            if (label.Length < 2) continue;
            items.Add(new LinkItem(label));
        }
        return items;
    }

    private static List<string> ExtractText(IReadOnlyList<ElementIndexer.IndexedNode> nodes)
    {
        var items = new List<string>();
        foreach (var n in nodes)
        {
            if (n.Element.Type != VisualElementType.Label) continue;
            var t = n.Element.GetText(maxLength: 500);
            if (!string.IsNullOrWhiteSpace(t)) items.Add(t.Trim());
        }
        return items;
    }

    private sealed record LinkItem(string Text);
}
