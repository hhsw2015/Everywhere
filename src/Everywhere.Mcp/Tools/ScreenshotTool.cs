using System.ComponentModel;
using System.Text.Json;
using Everywhere.Interop;
using Everywhere.Mcp.Input;
using Everywhere.Mcp.Snapshot;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class ScreenshotTool
{
    [McpServerTool(Name = "screenshot", ReadOnly = true)]
    [Description(
        "Base64 screenshot. Target: element_index → app_hint → focused window. " +
        "Defaults jpeg/70/1920x1080 (~25-35K tokens). Use format=png quality=100 max_*=0 for OCR/diff. " +
        "raise_if_needed=true only for occluded/off-screen targets. " +
        "Returns {screenshot_png_b64, format}.")]
    public static async Task<CallToolResult> Screenshot(
        IVisualElementContext context,
        SessionStore sessions,
        FocusBorrow focusBorrow,
        CancellationToken cancellationToken,
        string? element_index = null,
        string? app_hint = null,
        string? format = null,
        int? quality = null,
        int? max_height = null,
        int? max_width = null,
        bool raise_if_needed = false)
    {
        try
        {
            IVisualElement? target;
            int processId = 0;
            nint windowHandle = 0;

            if (!string.IsNullOrEmpty(element_index))
            {
                var (error, element) = ElementResolver.Resolve(sessions, element_index);
                if (error is not null) return error;
                target = element;
                processId = element!.ProcessId;
                windowHandle = element.NativeWindowHandle;
            }
            else if (!string.IsNullOrWhiteSpace(app_hint))
            {
                var resolved = AppResolver.Resolve(context, app_hint);
                if (resolved is null) return ToolErrors.AppNotRunning(app_hint);
                target = resolved.Value.Window;
                processId = resolved.Value.ProcessId;
                windowHandle = resolved.Value.Window.NativeWindowHandle;
            }
            else
            {
                var focused = context.FocusedElement;
                if (focused is null) return ToolErrors.NoFocusedApp();
                target = WalkToTopLevel(focused) ?? focused;
                processId = target.ProcessId;
                windowHandle = target.NativeWindowHandle;
            }

            using var borrow = raise_if_needed
                ? focusBorrow.Acquire(windowHandle, requireFocus: true, processId: processId)
                : null;

            // Use record defaults (Quality=70, MaxHeight=1080, MaxWidth=1920)
            // unless caller overrides. ScreenshotEncoder.cs is the source of truth.
            var opts = new ScreenshotEncodeOptions(
                Format: ParseFormat(format),
                Quality: quality ?? 70,
                MaxHeight: max_height ?? 1080,
                MaxWidth: max_width ?? 1920);

            using var captured = await target!.CaptureAsync(cancellationToken);
            var base64 = ScreenshotEncoder.EncodeBase64(captured, opts);

            var payload = JsonSerializer.Serialize(new
            {
                screenshot_png_b64 = base64,
                format = opts.Format == ScreenshotFormat.Png ? "png" : "jpeg",
            });
            return new CallToolResult { Content = [new TextContentBlock { Text = payload }] };
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "screenshot");
        }
    }

    private static ScreenshotFormat ParseFormat(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ScreenshotFormat.Jpeg;
        return raw.ToLowerInvariant() switch
        {
            "png" => ScreenshotFormat.Png,
            _ => ScreenshotFormat.Jpeg,
        };
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
