using System.ComponentModel;
using Everywhere.Interop;
using Everywhere.Mcp.Input;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class DragTool
{
    [McpServerTool(Name = "drag")]
    [Description("Press at (from_x, from_y), drag to (to_x, to_y), then release. Coordinates are screen pixels; the target window will be brought to the foreground first.")]
    public static CallToolResult Drag(
        string app,
        double from_x,
        double from_y,
        double to_x,
        double to_y,
        IInputSimulator input,
        FocusBorrow focusBorrow,
        IVisualElementContext context,
        Everywhere.Mcp.CursorOverlay.ITargetWindowHighlighter highlighter,
        IAxBridgeBackend? backend = null)
    {
        if (backend is not null)
        {
            var (text, isError) = backend.Drag(app, from_x, from_y, to_x, to_y);
            return isError
                ? ToolErrors.Error(text)
                : new CallToolResult { Content = [new TextContentBlock { Text = text }] };
        }

        var resolved = AppResolver.Resolve(context, app);
        if (resolved is null) return ToolErrors.AppNotRunning(app);

        try
        {
            using var _ = focusBorrow.Acquire(
                resolved.Value.Window.NativeWindowHandle,
                requireFocus: true,
                processId: resolved.Value.ProcessId);
            highlighter.Highlight(resolved.Value.Window.BoundingRectangle, $"Everywhere · {app}");
            input.DragTo(from_x, from_y, to_x, to_y, targetPid: resolved.Value.ProcessId);
            return new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "drag");
        }
    }
}
