using System.ComponentModel;
using Avalonia.Input;
using Everywhere.Interop;
using Everywhere.Mcp.Input;
using Everywhere.Mcp.Snapshot;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class ScrollTool
{
    private const int MaxPages = 100;

    [McpServerTool(Name = "scroll")]
    [Description("Scroll an indexed element by a number of pages. direction is 'up'|'down'|'left'|'right'; pages defaults to 1 (max 100, fractional values round to nearest int >=1). Uses CGEvent scroll wheel events on macOS (mirrors OCCU scrollTargeted/Globally) and falls back to PageUp/Down keyboard shortcuts if that fails.")]
    public static CallToolResult Scroll(
        string app,
        string element_index,
        string direction,
        SessionStore sessions,
        IInputSimulator input,
        FocusBorrow focusBorrow,
        IVisualElementContext context,
        IServiceProvider services,
        double? pages = null)
    {
        if (string.IsNullOrEmpty(element_index)) return ToolErrors.ParameterRequired("element_index");
        if (string.IsNullOrEmpty(direction)) return ToolErrors.ParameterRequired("direction");

        var dir = direction.ToLowerInvariant();
        if (dir is not ("up" or "down" or "left" or "right"))
            return ToolErrors.Error($"Invalid direction '{direction}'. Expected up|down|left|right.");

        var amount = pages ?? 1.0;
        if (double.IsNaN(amount) || amount <= 0 || amount > MaxPages)
            return ToolErrors.Error($"pages must be in (0, {MaxPages}].");

        var backend = services.GetService(typeof(IAxBridgeBackend)) as IAxBridgeBackend;
                if (backend is not null)
        {
            var (txt, isError) = backend.Scroll(app, dir, element_index, amount);
            return isError ? ToolErrors.Error(txt) : new CallToolResult { Content = [new TextContentBlock { Text = txt }] };
        }

        var (error, element) = ElementResolver.Resolve(sessions, element_index, appHint: app);
        if (error is not null) return error;

        // Primary path: CGEvent scroll wheel at the element's center.
        // Mirrors OCCU scrollTargeted (CGEventCreateScrollWheelEvent2 +
        // postToPid). 4 directions supported; works on AppKit + SwiftUI.
        try
        {
            var rect = element!.BoundingRectangle;
            if (rect.Width > 0 && rect.Height > 0)
            {
                var cx = rect.X + rect.Width / 2.0;
                var cy = rect.Y + rect.Height / 2.0;
                var resolved = AppResolver.Resolve(context, app);
                IDisposable? focusHandle = resolved is not null
                    ? focusBorrow.Acquire(resolved.Value.Window.NativeWindowHandle, requireFocus: true, processId: resolved.Value.ProcessId)
                    : null;
                try
                {
                    input.Scroll(cx, cy, dir, amount, targetPid: resolved?.ProcessId);
                }
                finally { focusHandle?.Dispose(); }
                return new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
            }
        }
        catch
        {
            // Fall through to keyboard fallback.
        }

        // Fallback: PageUp/PageDown via element's own SendShortcut. No
        // horizontal equivalent so left/right error out here.
        KeyboardShortcut? shortcut = dir switch
        {
            "up" => new KeyboardShortcut(Key.PageUp, KeyModifiers.None),
            "down" => new KeyboardShortcut(Key.PageDown, KeyModifiers.None),
            _ => null,
        };
        if (shortcut is null)
            return ToolErrors.Error($"Horizontal scroll fallback not available for {dir}.");

        try
        {
            var iterations = Math.Clamp((int)Math.Round(amount), 1, MaxPages);
            for (var i = 0; i < iterations; i++)
                element!.SendShortcut(shortcut.Value);
            return new CallToolResult { Content = [new TextContentBlock { Text = "ok (keyboard fallback)" }] };
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "scroll");
        }
    }
}
