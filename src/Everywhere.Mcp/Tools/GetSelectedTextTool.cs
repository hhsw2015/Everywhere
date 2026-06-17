using System.ComponentModel;
using System.Text.Json;
using Everywhere.Interop;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class GetSelectedTextTool
{
    [McpServerTool(Name = "get_selected_text", ReadOnly = true)]
    [Description("Return the text the user has currently selected in any application, OS-wide, as JSON {\"selected\": bool, \"text\": string}. \"selected\" is false when nothing is highlighted; \"text\" is the empty string in that case. PREFER this over scraping a tree when the user says \"this text\", \"the highlighted code\", \"my selection\", \"选中的\".")]
    public static CallToolResult GetSelectedText(IVisualElementContext context)
    {
        var focused = context.FocusedElement;
        var text = focused?.GetSelectionText() ?? string.Empty;
        var payload = JsonSerializer.Serialize(new
        {
            selected = !string.IsNullOrEmpty(text),
            text,
        });
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = payload }],
        };
    }
}
