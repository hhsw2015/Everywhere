using System.ComponentModel;
using System.Text.Json;
using Everywhere.Mcp.Snapshot;
using Everywhere.Mcp.Tools.Schemas;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class ExpandElementTool
{
    [McpServerTool(Name = "expand_element", ReadOnly = true)]
    [Description("Re-walk the accessibility tree rooted at a previously indexed element with a fresh budget. Use when a prior get_app_state/get_focused_context reported omitted_children=true and you need to drill down into a specific subtree without re-snapshotting the whole window. The element_index values returned ARE addressable by subsequent tool calls (click, set_value, etc.) — they replace the prior snapshot's index map for the same app.")]
    public static CallToolResult ExpandElement(
        string element_index,
        SessionStore sessions,
        int? budget = null)
    {
        try
        {
            if (string.IsNullOrEmpty(element_index)) return ToolErrors.ParameterRequired("element_index");

            var (error, element) = ElementResolver.Resolve(sessions, element_index);
            if (error is not null) return error;

            var nodeBudget = Math.Clamp(budget ?? 2000, 1, UpstreamConstants.AccessibilityTreeMaxNodeCount * 4);
            var nodes = ElementIndexer.Walk(element!, maxNodeCount: Math.Min(nodeBudget, UpstreamConstants.AccessibilityTreeMaxNodeCount));

            var elementMap = ElementIndexer.ToIndexMap(nodes);
            var appKey = AppKey.FromProcessId(element!.ProcessId);
            sessions.Issue(appKey, elementMap, element.NativeWindowHandle);

            var bounds = element.BoundingRectangle;
            var payload = new FocusedContextResult
            {
                App = appKey,
                WindowTitle = element.Name,
                WindowBounds = new WindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height),
                TreeText = SnapshotRenderer.Render(nodes, showFullText: false),
                // ponytail: TreeJson duplicates TreeText. Opt-in only.
                TreeJson = Environment.GetEnvironmentVariable("EVERYWHERE_INCLUDE_TREE_JSON") == "1"
                    ? TreeJsonBuilder.Build(nodes)
                    : null,
            };
            SemanticEnricher.Apply(payload, nodes);

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload) }],
            };
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "expand_element");
        }
    }
}
