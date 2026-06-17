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
        string? mode,
        IVisualElementContext context,
        SessionStore sessions)
    {
        var screenSelectionMode = ParseMode(mode);

        IVisualElement? picked;
        try
        {
            picked = await context.PickVisualElementAsync(screenSelectionMode);
        }
        catch (Exception ex)
        {
            return ToolErrors.Error($"pick_element failed: {ex.Message}");
        }

        if (picked is null)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = "{\"cancelled\":true}" }],
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
            TreeJson = TreeJsonBuilder.Build(nodes),
        };

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload) }],
        };
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
