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
        "get_focused_context when pinned is false.")]
    public static CallToolResult ReadPick(PickStash stash, SessionStore sessions)
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
        var payload = new FocusedContextResult
        {
            App = appKey,
            WindowTitle = picked.Name,
            WindowBounds = new WindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            FocusedSummary = picked.GetText(maxLength: UpstreamConstants.SnapshotTextDefaultCharacterLimit),
            TreeText = SnapshotRenderer.Render(nodes, showFullText: false),
            TreeJson = TreeJsonBuilder.Build(nodes),
        };

        var json = JsonSerializer.Serialize(new
        {
            pinned = true,
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
            return ToolErrors.FromException(ex, "read_pick");
        }
    }
}
