using System.ComponentModel;
using Everywhere.Interop;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class GetSelectedTextTool
{
    [McpServerTool(Name = "get_selected_text", ReadOnly = true)]
    [Description("Return the text the user has currently selected in any application, OS-wide. Returns the empty string when nothing is selected. PREFER THIS over scraping a tree when the user says \"this text\", \"the highlighted code\", \"my selection\", \"选中的\".")]
    public static CallToolResult GetSelectedText(IVisualElementContext context)
    {
        var focused = context.FocusedElement;
        var text = focused?.GetSelectionText() ?? string.Empty;
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }],
        };
    }
}
