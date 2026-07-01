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
        "OS-wide highlighted text as {selected, text, app, source}. " +
        "2-min cache survives focus change (select in browser, ask in chat still works); " +
        "falls back to focused element's selection.")]
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
