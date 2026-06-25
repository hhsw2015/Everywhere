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
    [Description("Click a UI element. Pass element_index from a prior get_app_state when the target is in the indexed tree (no pointer movement, target window need not be foreground). Pass x/y screen pixel coordinates for free-form clicks (no foreground swap by default — the click is delivered via CGEventPostToPid; set EVERYWHERE_ALLOW_GLOBAL_POINTER_FALLBACKS=1 to use the global HID tap, which raises the target first). click_count defaults to 1; mouse_button defaults to left.")]
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

                highlighter.Highlight(resolved.Value.Window.BoundingRectangle,
                    $"Everywhere · {app}");

                // 1:1 OCCU x/y click (ComputerUseService.swift L430-475).
                // First: try every AX candidate at the point (bestElement
                // + AXHitTest). Each candidate runs through
                // performPreferredClick. Fall through to performNonAXClick-
                // Fallback only when every AX candidate refuses.
                //
                // OCCU does NOT prepareAppForGlobalPointerInput on this
                // path; targeted PostToPid carries the event regardless
                // of frontmost. Match: no FocusBorrow Acquire.
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

                // Coord fallback. OCCU default = clickTargeted
                // (postToPid). Global gated by env, same as the
                // element-index path in ElementClickDispatcher.
                var allowGlobal = Environment.GetEnvironmentVariable(
                    "EVERYWHERE_ALLOW_GLOBAL_POINTER_FALLBACKS") == "1";
                if (allowGlobal)
                {
                    using var _ = focusBorrow.Acquire(
                        resolved.Value.Window.NativeWindowHandle,
                        requireFocus: true,
                        processId: resolved.Value.ProcessId);
                    input.Click(x.Value, y.Value, clickCount, button, targetPid: null);
                }
                else
                {
                    input.Click(x.Value, y.Value, clickCount, button,
                        targetPid: resolved.Value.ProcessId);
                }
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
