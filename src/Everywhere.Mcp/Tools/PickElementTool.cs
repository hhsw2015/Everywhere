using System.ComponentModel;
using System.Text.Json;
using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using Everywhere.Mcp.Tools.Schemas;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class PickElementTool
{
    [McpServerTool(Name = "pick_element")]
    [Description("Trigger Everywhere's visual element picker. The user clicks the element/window/screen they want and the tool returns its element_index plus a fresh snapshot of the surrounding tree. Returns {\"cancelled\":true} if the user dismisses the picker. PREFER THIS over guessing coordinates when the user says \"this thing\", \"that button\", \"this window\".")]
    public static async Task<CallToolResult> PickElement(
        IVisualElementContext context,
        SessionStore sessions,
        string? mode = null)
    {
        try
        {
            var screenSelectionMode = ParseMode(mode);
            var picked = await context.PickVisualElementAsync(screenSelectionMode);
            if (picked is null)
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock
                    {
                        Text = "{\"cancelled\":true,\"picked_index\":null,\"app\":null,\"element\":null}",
                    }],
                };
            }

            var nodes = ElementIndexer.Walk(picked);
            var elementMap = ElementIndexer.ToIndexMap(nodes);
            var appKey = AppKey.FromProcessId(picked.ProcessId);
            sessions.Issue(appKey, elementMap, picked.NativeWindowHandle);

            var bounds = picked.BoundingRectangle;
            var payload = new FocusedContextResult
            {
                App = appKey,
                WindowTitle = picked.Name,
                WindowBounds = new WindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height),
                TreeText = SnapshotRenderer.Render(nodes, showFullText: false),
                // ponytail: TreeJson duplicates TreeText. Opt-in only.
                TreeJson = Environment.GetEnvironmentVariable("EVERYWHERE_INCLUDE_TREE_JSON") == "1"
                    ? TreeJsonBuilder.Build(nodes)
                    : null,
            };
            SemanticEnricher.Apply(payload, nodes);

            var json = JsonSerializer.Serialize(new
            {
                cancelled = false,
                picked_index = nodes.Count > 0 ? nodes[0].Index : 0,
                app = appKey,
                element = payload,
            });
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = json }],
            };
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "pick_element");
        }
    }

    private static ScreenSelectionMode? ParseMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return null;
        return mode.ToLowerInvariant() switch
        {
            "element" => ScreenSelectionMode.Element,
            "window" => ScreenSelectionMode.Window,
            "screen" => ScreenSelectionMode.Screen,
            "free" => ScreenSelectionMode.Free,
            _ => null,
        };
    }
}
