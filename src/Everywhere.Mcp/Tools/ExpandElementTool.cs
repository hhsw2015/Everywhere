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
    [Description("Re-walk the accessibility tree rooted at a previously indexed element with a fresh budget. Use when a prior get_app_state/get_focused_context reported omitted_children=true and you need to drill down into a specific subtree without re-snapshotting the whole window.")]
    public static CallToolResult ExpandElement(
        string element_index,
        int? budget,
        SessionStore sessions)
    {
        if (string.IsNullOrEmpty(element_index)) return ToolErrors.ParameterRequired("element_index");

        var (error, element) = ElementResolver.Resolve(sessions, element_index);
        if (error is not null) return error;

        var nodeBudget = Math.Clamp(budget ?? 2000, 1, UpstreamConstants.AccessibilityTreeMaxNodeCount * 4);
        var nodes = ElementIndexer.Walk(element!, maxNodeCount: Math.Min(nodeBudget, UpstreamConstants.AccessibilityTreeMaxNodeCount));

        var bounds = element!.BoundingRectangle;
        var payload = new FocusedContextResult
        {
            WindowTitle = element.Name,
            WindowBounds = new WindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            TreeText = SnapshotRenderer.Render(nodes, showFullText: false),
            TreeJson = TreeJsonBuilder.Build(nodes),
        };

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload) }],
        };
    }
}
