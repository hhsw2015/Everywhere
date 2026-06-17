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
    [Description(
        "Capture a base64-encoded screenshot. With element_index, captures only that element's " +
        "bounding box; without it, captures the focused window. " +
        "Defaults: format=jpeg, quality=80, max_height=1080 — yields ~150-300 KB " +
        "(~50-75 K agent tokens) vs. legacy PNG ~1 MB (~330 K tokens). " +
        "Pass format=\"png\" + quality=100 + max_height=0 when you need bit-perfect output (OCR / diff). " +
        "Returns {\"screenshot_png_b64\":\"...\",\"format\":\"jpeg|png\",\"width\":N,\"height\":N}. " +
        "Field name kept as `screenshot_png_b64` for client back-compat — actual format is in `format`.")]
    public static async Task<CallToolResult> Screenshot(
        IVisualElementContext context,
        SessionStore sessions,
        CancellationToken cancellationToken,
        string? element_index = null,
        string? format = null,
        int? quality = null,
        int? max_height = null)
    {
        try
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

            var opts = new ScreenshotEncodeOptions(
                Format: ParseFormat(format),
                Quality: quality ?? 80,
                MaxHeight: max_height ?? 1080);

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
