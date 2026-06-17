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
    [McpServerTool(Name = "scroll")]
    [Description("Scroll an indexed element by a number of pages. direction must be one of up, down, left, right; pages defaults to 1.0 (positive only).")]
    public static CallToolResult Scroll(
        string app,
        string element_index,
        string direction,
        double? pages,
        SessionStore sessions)
    {
        if (string.IsNullOrEmpty(element_index)) return ToolErrors.ParameterRequired("element_index");
        if (string.IsNullOrEmpty(direction)) return ToolErrors.ParameterRequired("direction");

        if (!IsValidDirection(direction))
        {
            return ToolErrors.Error($"Invalid direction '{direction}'. Expected up|down|left|right.");
        }

        var (error, element) = ElementResolver.Resolve(sessions, element_index);
        if (error is not null) return error;

        var amount = pages ?? 1.0;
        if (amount <= 0)
        {
            return ToolErrors.Error("pages must be positive.");
        }

        try
        {
            var shortcut = direction.ToLowerInvariant() switch
            {
                "up" => new KeyboardShortcut(Key.PageUp, KeyModifiers.None),
                "down" => new KeyboardShortcut(Key.PageDown, KeyModifiers.None),
                "left" => new KeyboardShortcut(Key.Home, KeyModifiers.None),
                "right" => new KeyboardShortcut(Key.End, KeyModifiers.None),
                _ => throw new InvalidOperationException(),
            };

            var iterations = Math.Max(1, (int)Math.Round(amount));
            for (var i = 0; i < iterations; i++)
            {
                element!.SendShortcut(shortcut);
            }

            return new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
        }
        catch (Exception ex)
        {
            return ToolErrors.Error($"Failed to scroll: {ex.Message}");
        }
    }

    private static bool IsValidDirection(string direction) =>
        direction.Equals("up", StringComparison.OrdinalIgnoreCase)
        || direction.Equals("down", StringComparison.OrdinalIgnoreCase)
        || direction.Equals("left", StringComparison.OrdinalIgnoreCase)
        || direction.Equals("right", StringComparison.OrdinalIgnoreCase);
}
