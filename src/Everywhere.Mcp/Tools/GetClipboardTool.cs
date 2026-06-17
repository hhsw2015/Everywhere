using System.ComponentModel;
using System.Text.Json;
using Everywhere.Mcp.Input;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class GetClipboardTool
{
    [McpServerTool(Name = "get_clipboard", ReadOnly = true)]
    [Description(
        "Return the current system clipboard content as JSON " +
        "{\"has_text\": bool, \"text\": string}. has_text=false / text=\"\" when " +
        "the clipboard is empty or contains non-text data (image / file). " +
        "Doesn't modify the clipboard. " +
        "Use when the user references \"the thing I just copied\", \"clipboard\", \"剪贴板\".")]
    public static CallToolResult GetClipboard(IClipboardReader reader)
    {
        try
        {
            var text = reader.GetText() ?? string.Empty;
            return new CallToolResult
            {
                Content = [new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(new { has_text = !string.IsNullOrEmpty(text), text }),
                }],
            };
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "get_clipboard");
        }
    }
}
