using System.ComponentModel;
using Everywhere.Interop;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class GetTerminalOutputTool
{
    private const int MaxLinesBack = 10_000;
    private const int DefaultLinesBack = 200;

    [McpServerTool(Name = "get_terminal_output", ReadOnly = true)]
    [Description("Return the visible/rendered text of the currently focused terminal window (Terminal, iTerm2, Windows Terminal, gnome-terminal, etc). Returns the empty string if the focused app is not a terminal. Use this when the user references \"this output\", \"this error\", \"the last command\", \"刚才那条\".")]
    public static CallToolResult GetTerminalOutput(
        [Description("Max number of trailing lines to return. Defaults to 200, clamped to [1, 10000].")] int? lines_back,
        IVisualElementContext context)
    {
        var maxLines = Math.Clamp(lines_back ?? DefaultLinesBack, 1, MaxLinesBack);

        var focused = context.FocusedElement;
        if (focused is null)
        {
            return new CallToolResult { Content = [new TextContentBlock { Text = string.Empty }] };
        }

        try
        {
            var text = focused.GetText(maxLength: -1) ?? string.Empty;
            var lines = text.Split('\n');
            if (lines.Length > maxLines)
            {
                text = string.Join('\n', lines[^maxLines..]);
            }
            return new CallToolResult { Content = [new TextContentBlock { Text = text }] };
        }
        catch (Exception ex)
        {
            return ToolErrors.Error($"get_terminal_output failed: {ex.Message}");
        }
    }
}
