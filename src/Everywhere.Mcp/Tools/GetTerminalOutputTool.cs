using System.ComponentModel;
using System.Text.Json;
using Everywhere.Interop;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class GetTerminalOutputTool
{
    private const int MaxLinesBack = 10_000;
    private const int DefaultLinesBack = 200;
    // Average terminal line width ~200 chars; cap text reads to avoid materialising
    // multi-megabyte scrollback strings.
    private const int AverageLineCapBytes = 200;

    [McpServerTool(Name = "get_terminal_output", ReadOnly = true)]
    [Description(
        "Return the visible/rendered text of the focused terminal as JSON " +
        "{\"is_terminal\": bool, \"lines_returned\": int, \"text\": string}. " +
        "\"is_terminal\" is false when the focused app is not a recognised terminal — " +
        "the agent should then fall back to get_focused_context. " +
        "Use this when the user references \"this output\", \"this error\", \"the last command\", \"刚才那条\".")]
    public static CallToolResult GetTerminalOutput(
        IVisualElementContext context,
        [Description("Max number of trailing lines to return. Defaults to 200, clamped to [1, 10000].")] int? lines_back = null)
    {
        var maxLines = Math.Clamp(lines_back ?? DefaultLinesBack, 1, MaxLinesBack);

        var focused = context.FocusedElement;
        if (focused is null || !LooksLikeTerminal(focused))
        {
            return Json(new
            {
                is_terminal = false,
                lines_returned = 0,
                text = string.Empty,
            });
        }

        try
        {
            var maxBytes = maxLines * AverageLineCapBytes;
            var text = focused.GetText(maxLength: maxBytes) ?? string.Empty;
            var lines = text.Split('\n');
            var slice = lines.Length > maxLines ? lines[^maxLines..] : lines;
            return Json(new
            {
                is_terminal = true,
                lines_returned = slice.Length,
                text = string.Join('\n', slice),
            });
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "get_terminal_output");
        }
    }

    private static bool LooksLikeTerminal(IVisualElement element)
    {
        // Walk up to the top-level and inspect the process name. Terminal apps include
        // Terminal, iTerm2, Ghostty, Warp, Alacritty, kitty, Windows Terminal, gnome-terminal,
        // konsole, xterm, etc.
        var top = element;
        while (top.Parent is not null) top = top.Parent;
        var key = Snapshot.AppKey.FromProcessId(top.ProcessId);
        return key.Contains("term", StringComparison.OrdinalIgnoreCase)
            || key.Contains("iterm", StringComparison.OrdinalIgnoreCase)
            || key.Contains("ghostty", StringComparison.OrdinalIgnoreCase)
            || key.Contains("warp", StringComparison.OrdinalIgnoreCase)
            || key.Contains("alacritty", StringComparison.OrdinalIgnoreCase)
            || key.Contains("kitty", StringComparison.OrdinalIgnoreCase)
            || key.Contains("konsole", StringComparison.OrdinalIgnoreCase)
            || key.Contains("xterm", StringComparison.OrdinalIgnoreCase);
    }

    private static CallToolResult Json(object payload) =>
        new()
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload) }],
        };
}
