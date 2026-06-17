using System.ComponentModel;
using Avalonia.Input;
using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class ScrollTool
{
    private const int MaxPages = 100;

    [McpServerTool(Name = "scroll")]
    [Description("Scroll an indexed element vertically by a number of pages. direction must be 'up' or 'down'; pages defaults to 1 (max 100, fractional values round to nearest int >=1). Horizontal scrolling is not yet supported — pass left/right and you'll get an error.")]
    public static CallToolResult Scroll(
        string app,
        string element_index,
        string direction,
        double? pages,
        SessionStore sessions)
    {
        if (string.IsNullOrEmpty(element_index)) return ToolErrors.ParameterRequired("element_index");
        if (string.IsNullOrEmpty(direction)) return ToolErrors.ParameterRequired("direction");

        KeyboardShortcut? shortcut = direction.ToLowerInvariant() switch
        {
            "up" => new KeyboardShortcut(Key.PageUp, KeyModifiers.None),
            "down" => new KeyboardShortcut(Key.PageDown, KeyModifiers.None),
            "left" or "right" => null,
            _ => null,
        };

        if (shortcut is null)
        {
            return direction.Equals("left", StringComparison.OrdinalIgnoreCase)
                || direction.Equals("right", StringComparison.OrdinalIgnoreCase)
                ? ToolErrors.Error("Horizontal scrolling is not supported yet. Pass 'up' or 'down'.")
                : ToolErrors.Error($"Invalid direction '{direction}'. Expected up|down.");
        }

        var (error, element) = ElementResolver.Resolve(sessions, element_index);
        if (error is not null) return error;

        var amount = pages ?? 1.0;
        if (double.IsNaN(amount) || amount <= 0 || amount > MaxPages)
        {
            return ToolErrors.Error($"pages must be in (0, {MaxPages}].");
        }

        try
        {
            var iterations = Math.Clamp((int)Math.Round(amount), 1, MaxPages);
            for (var i = 0; i < iterations; i++)
            {
                element!.SendShortcut(shortcut.Value);
            }

            return new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
        }
        catch (Exception ex)
        {
            return ToolErrors.Error($"Failed to scroll: {ex.Message}");
        }
    }
}
