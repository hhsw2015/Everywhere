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
        SessionStore sessions,
        IInputSimulator input,
        FocusBorrow focusBorrow,
        IVisualElementContext context,
        Everywhere.Mcp.CursorOverlay.ITargetWindowHighlighter highlighter,
        string? element_index = null,
        double? x = null,
        double? y = null,
        int? click_count = null,
        string? mouse_button = null)
    {
        try
        {
            if (!string.IsNullOrEmpty(element_index))
            {
                var (error, element) = ElementResolver.Resolve(sessions, element_index, appHint: app);
                if (error is not null) return error;

                var btn = ParseButton(mouse_button);
                var cc = click_count is { } cnt && cnt > 0 ? cnt : 1;
                return ElementClickDispatcher.Click(element!, input, focusBorrow, context, app, highlighter, cc, btn);
            }

            if (x.HasValue && y.HasValue)
            {
                var resolved = AppResolver.Resolve(context, app);
                if (resolved is null) return ToolErrors.AppNotRunning(app);

                var button = ParseButton(mouse_button);
                var clickCount = click_count is { } c && c > 0 ? c : 1;

                // OCCU parity: targeted CGEventPostToPid keeps the user's
                // real cursor + global keyboard state untouched. We still
                // acquire focus (some apps still gate on AXMain/foreground)
                // but the click itself is delivered straight to the target
                // process so the user can keep using other apps in parallel.
                using var _ = focusBorrow.Acquire(
                    resolved.Value.Window.NativeWindowHandle,
                    requireFocus: true,
                    processId: resolved.Value.ProcessId);
                highlighter.Highlight(resolved.Value.Window.BoundingRectangle,
                    $"Everywhere · {app}");
                input.Click(x.Value, y.Value, clickCount, button, targetPid: resolved.Value.ProcessId);
                return new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
            }

            return ToolErrors.Error("click requires either element_index or both x and y.");
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "click");
        }
    }

    private static MouseButton ParseButton(string? raw) =>
        raw?.ToLowerInvariant() switch
        {
            "right" => MouseButton.Right,
            "middle" => MouseButton.Middle,
            _ => MouseButton.Left,
        };
}
