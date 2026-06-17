using System.ComponentModel;
using System.Text.Json;
using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class GetSelectedTextTool
{
    [McpServerTool(Name = "get_selected_text", ReadOnly = true)]
    [Description(
        "Return the text the user has highlighted, OS-wide, as JSON " +
        "{\"selected\": bool, \"text\": string, \"app\": string|null, \"source\": \"cache\"|\"focused\"|null}. " +
        "Pulls from a 2-minute selection cache fed by the platform's text-selection observer, so it " +
        "works even when focus has since moved to a different app (e.g. you select in the browser, " +
        "switch back to chat, ask the agent). Falls back to the currently focused element's selection " +
        "if no cached selection is fresh. selected=false / text=\"\" when nothing is highlighted anywhere.")]
    public static CallToolResult GetSelectedText(IVisualElementContext context, SelectionCache cache)
    {
        try
        {
            // Cache wins — survives focus changes back to the chat window.
            if (cache.GetFresh() is { } cached)
            {
                return Json(new
                {
                    selected = true,
                    text = cached.Text,
                    app = cached.AppKey,
                    source = "cache",
                });
            }

            var focused = context.FocusedElement;
            var text = focused?.GetSelectionText() ?? string.Empty;
            var hasSelection = !string.IsNullOrEmpty(text);
            return Json(new
            {
                selected = hasSelection,
                text,
                app = hasSelection && focused != null ? AppKey.FromProcessId(focused.ProcessId) : null,
                source = hasSelection ? "focused" : (string?)null,
            });
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "get_selected_text");
        }
    }

    private static CallToolResult Json(object payload) =>
        new()
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload) }],
        };
}
