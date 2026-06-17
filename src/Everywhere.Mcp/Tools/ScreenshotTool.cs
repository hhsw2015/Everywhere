using System.ComponentModel;
using System.Text.Json;
using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class ScreenshotTool
{
    [McpServerTool(Name = "screenshot", ReadOnly = true)]
    [Description("Capture a PNG screenshot. With element_index, captures only that element's bounding box; without it, captures the focused window. Compression and size envelope match get_app_state. Returns {\"png_b64\": \"...\"}.")]
    public static async Task<CallToolResult> Screenshot(
        IVisualElementContext context,
        SessionStore sessions,
        CancellationToken cancellationToken,
        string? element_index = null)
    {
        IVisualElement? target;
        if (!string.IsNullOrEmpty(element_index))
        {
            var (error, element) = ElementResolver.Resolve(sessions, element_index);
            if (error is not null) return error;
            target = element;
        }
        else
        {
            var focused = context.FocusedElement;
            if (focused is null) return ToolErrors.NoFocusedApp();
            target = WalkToTopLevel(focused) ?? focused;
        }

        try
        {
            using var captured = await target!.CaptureAsync(cancellationToken);
            var base64 = ScreenshotEncoder.EncodePngBase64(captured);
            var payload = JsonSerializer.Serialize(new { screenshot_png_b64 = base64 });
            return new CallToolResult { Content = [new TextContentBlock { Text = payload }] };
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "screenshot");
        }
    }

    private static IVisualElement? WalkToTopLevel(IVisualElement element)
    {
        var current = element;
        while (current != null && current.Type != VisualElementType.TopLevel)
        {
            current = current.Parent;
        }
        return current;
    }
}
