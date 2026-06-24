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

                using var _ = focusBorrow.Acquire(
                    resolved.Value.Window.NativeWindowHandle,
                    requireFocus: true,
                    processId: resolved.Value.ProcessId);
                highlighter.Highlight(resolved.Value.Window.BoundingRectangle,
                    $"Everywhere · {app}");

                // OCCU x/y path (CUS L430-470): first try AX click on
                // candidates at the point — bestElement (smallest
                // containing) and hitTestElement (raw AXHitTest). Each
                // candidate runs through performAXClickSequence with
                // includeNearbyHitTesting=false (the point IS the hit).
                // Falls back to coordinate CGEvent only when every AX
                // candidate fails.
                var ix = (int)Math.Round(x.Value);
                var iy = (int)Math.Round(y.Value);
                var hit = context.ElementFromPoint(new Avalonia.PixelPoint(ix, iy));
                if (hit is not null)
                {
                    try
                    {
                        if (button == MouseButton.Right && hit.TryInvokeAction("showmenu"))
                            return new CallToolResult { Content = [new TextContentBlock { Text = "ok (x/y → AX hit + ShowMenu)" }] };
                        if (button == MouseButton.Left)
                        {
                            try { hit.Invoke(clickCount); return new CallToolResult { Content = [new TextContentBlock { Text = "ok (x/y → AX hit invoke)" }] }; }
                            catch { /* fall through to coord */ }
                        }
                    }
                    catch { /* AX failed → coord fallback */ }
                }

                // Coordinate fallback. Use HidEventTap (targetPid=null)
                // so SwiftUI gestures fire — see v0.9.56 commit.
                input.Click(x.Value, y.Value, clickCount, button, targetPid: null);
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
