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
        "Read the UI element the user explicitly pinned for this agent via the Agent Pick hotkey " +
        "(see Settings → Shortcut → 'Pin Element for AI Agent'). Returns {\"pinned\": false} if " +
        "the user hasn't pinned anything; the agent should then fall back to get_focused_context " +
        "or list_apps. Reading consumes the pin — the next call returns {\"pinned\": false}. " +
        "PREFER calling this BEFORE get_focused_context/list_apps when the user uses deictic " +
        "references like \"this\", \"that\", \"the button I just selected\", \"刚才那个\".")]
    public static CallToolResult ReadPick(PickStash stash, SessionStore sessions)
    {
        var picked = stash.Take();
        if (picked is null)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = "{\"pinned\":false}" }],
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

        var json = JsonSerializer.Serialize(new { pinned = true, element = payload });
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = json }],
        };
    }
}
