using System.ComponentModel;
using Everywhere.Interop;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class GetTerminalOutputTool
{
    [McpServerTool(Name = "get_terminal_output", ReadOnly = true)]
    [Description("Return the visible/rendered text of the currently focused terminal window (Terminal, iTerm2, Windows Terminal, gnome-terminal, etc). Returns the empty string if the focused app is not a terminal. Use this when the user references \"this output\", \"this error\", \"the last command\", \"刚才那条\".")]
    public static CallToolResult GetTerminalOutput(
        int? lines_back,
        IVisualElementContext context)
    {
        var focused = context.FocusedElement;
        if (focused is null)
        {
            return new CallToolResult { Content = [new TextContentBlock { Text = string.Empty }] };
        }

        // ponytail: read whatever the a11y layer surfaces from the focused terminal pane;
        // the GUI host's IVisualElementContext already returns the terminal's TextEdit element
        // when the user is in a terminal app. PTY-level capture lands in §13 (v2 follow-up).
        var maxLines = lines_back ?? 200;
        var text = focused.GetText(maxLength: -1) ?? string.Empty;

        if (maxLines > 0)
        {
            var lines = text.Split('\n');
            if (lines.Length > maxLines)
            {
                text = string.Join('\n', lines[^maxLines..]);
            }
        }

        return new CallToolResult { Content = [new TextContentBlock { Text = text }] };
    }
}
