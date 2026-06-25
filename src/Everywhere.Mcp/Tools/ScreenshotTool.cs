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
        "Capture a base64-encoded screenshot. Targets in priority order: element_index → app_hint " +
        "→ focused window. " +
        "Defaults: format=jpeg, quality=70, max_height=1080, max_width=1920 — keeps a 5K-display " +
        "window at ~70-100 KB (~25-35 K agent tokens) vs. PNG-100 ~3 MB (~1 M tokens). " +
        "Pass format=\"png\" + quality=100 + max_height=0 + max_width=0 when you need bit-perfect output (OCR / diff). " +
        "raise_if_needed: When true, briefly raise the target to the foreground before capture and " +
        "restore the previous foreground. Use only when the target may be obscured / off-screen / not " +
        "actively rendered (background tabs in some apps, occluded windows). " +
        "Returns {\"screenshot_png_b64\":\"...\",\"format\":\"jpeg|png\"}. " +
        "Field name kept as `screenshot_png_b64` for client back-compat — actual format is in `format`.")]
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
