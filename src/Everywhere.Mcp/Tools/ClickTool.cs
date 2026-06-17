using System.ComponentModel;
using Everywhere.Interop;
using Everywhere.Mcp.Input;
using Everywhere.Mcp.Snapshot;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class ClickTool
{
    [McpServerTool(Name = "click")]
    [Description("Click on a UI element. Pass element_index from a prior get_app_state when the target is in the indexed tree (no pointer movement, target window need not be foreground). Pass x/y screen pixel coordinates for free-form clicks (the target window will be brought to the foreground first). click_count defaults to 1; mouse_button defaults to left.")]
    public static CallToolResult Click(
        string app,
        string? element_index,
        double? x,
        double? y,
        int? click_count,
        string? mouse_button,
        SessionStore sessions,
        IInputSimulator input,
        FocusBorrow focusBorrow,
        IVisualElementContext context)
    {
        if (!string.IsNullOrEmpty(element_index))
        {
            var (error, element) = ElementResolver.Resolve(sessions, element_index, appHint: app);
            if (error is not null) return error;

            return ElementClickDispatcher.Click(element!);
        }

        if (x.HasValue && y.HasValue)
        {
            var resolved = AppResolver.Resolve(context, app);
            if (resolved is null) return ToolErrors.AppNotRunning(app);

            var button = ParseButton(mouse_button);
            var clickCount = click_count is { } c && c > 0 ? c : 1;

            try
            {
                using var _ = focusBorrow.Acquire(
                    resolved.Value.Window.NativeWindowHandle,
                    requireFocus: true,
                    processId: resolved.Value.ProcessId);
                input.Click(x.Value, y.Value, clickCount, button);
                return new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
            }
            catch (Exception ex)
            {
                return ToolErrors.FromException(ex, "click(x,y)");
            }
        }

        return ToolErrors.Error("click requires either element_index or both x and y.");
    }

    private static MouseButton ParseButton(string? raw) =>
        raw?.ToLowerInvariant() switch
        {
            "right" => MouseButton.Right,
            "middle" => MouseButton.Middle,
            _ => MouseButton.Left,
        };
}
