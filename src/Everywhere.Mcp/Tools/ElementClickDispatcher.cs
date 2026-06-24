using Everywhere.Interop;
using Everywhere.Mcp.Input;
using ModelContextProtocol.Protocol;

namespace Everywhere.Mcp.Tools;

/// <summary>
/// Routes "click this element" to the right semantic action based on the element's type.
/// Calling <see cref="IVisualElement.Invoke"/> on a slider / text field / list item often
/// returns a silent no-op while reporting success — surface that as a typed error instead
/// of fake "ok" so the agent can switch tools.
///
/// When the AX action chain (Press → Confirm → Open → ShowMenu) fails entirely
/// (Invoke throws), we fall back to a coordinate click on the element's centre.
/// Mirrors OCCU ComputerUseService.localClickActionPoints. Caller passes the
/// IInputSimulator + FocusBorrow it already holds so we don't take ownership.
/// </summary>
internal static class ElementClickDispatcher
{
    public static CallToolResult Click(
        IVisualElement element,
        IInputSimulator? input = null,
        FocusBorrow? focusBorrow = null,
        IVisualElementContext? context = null,
        string? appHint = null,
        Everywhere.Mcp.CursorOverlay.ITargetWindowHighlighter? highlighter = null,
        int clickCount = 1,
        MouseButton mouseButton = MouseButton.Left)
    {
        switch (element.Type)
        {
            case VisualElementType.TextEdit:
            case VisualElementType.Document:
                return ToolErrors.Error(
                    $"Cannot click element of type '{element.Type}'. Use set_value to change its text, " +
                    "or use coordinate click(x,y) to put the caret at a specific position.");

            case VisualElementType.Slider:
            case VisualElementType.Spinner:
                return ToolErrors.Error(
                    $"Cannot click element of type '{element.Type}'. Use set_value with a numeric value.");
        }

        // Run OCCU-style click-target redirection: a small trailing "Done"
        // button next to a row is almost never what the agent meant; an
        // Electron-web app row's synthetic-text child can't be Pressed
        // but the row above it can. ClickHeuristics returns the same
        // element when nothing matches.
        string? processName = null;
        try
        {
            if (element.ProcessId > 0)
            {
                processName = System.Diagnostics.Process.GetProcessById(element.ProcessId).ProcessName;
            }
        }
        catch { /* dead process / restricted — skip heuristics */ }
        element = ClickHeuristics.RedirectIfNeeded(element, processName);

        // Visual indicator on the target window (no-op when overlay
        // disabled). Walks up the AX tree to find the containing
        // AXWindow's screen rect so the highlight wraps the whole
        // window the agent is operating, not just the element.
        if (highlighter is not null)
        {
            try
            {
                var top = element;
                for (var hop = 0; hop < 16 && top.Parent is not null; hop++) top = top.Parent;
                highlighter.Highlight(top.BoundingRectangle,
                    string.IsNullOrEmpty(appHint) ? "Everywhere operating" : $"Everywhere · {appHint}");
            }
            catch { /* highlighter is best-effort */ }
        }

        // SwiftUI bypass: macOS 26+ first-party apps (Calculator, etc.)
        // accept AXPress with success status but the SwiftUI gesture
        // handler doesn't fire — only real CGEvent input does. Skip the
        // AX chain entirely and synthesize a coordinate click on those
        // processes.
        var preferCoord = ClickHeuristics.PrefersCoordinateClick(processName, null)
            && input is not null
            && context is not null
            && focusBorrow is not null
            && !string.IsNullOrEmpty(appHint);
        if (preferCoord)
        {
            var rectS = element.BoundingRectangle;
            if (rectS.Width > 0 && rectS.Height > 0)
            {
                try
                {
                    var cx = rectS.X + rectS.Width / 2.0;
                    var cy = rectS.Y + rectS.Height / 2.0;
                    AppResolver.ResolvedApp? resolved = AppResolver.Resolve(context!, appHint!);
                    // Indicator already fired up at the AX-walked
                    // top-window rect a few lines above; don't double-
                    // call here (would visually flicker the same border).
                    IDisposable? focusHandle = resolved is not null
                        ? focusBorrow!.Acquire(resolved.Value.Window.NativeWindowHandle, requireFocus: true, processId: resolved.Value.ProcessId)
                        : null;
                    try
                    {
                        // SwiftUI gesture handlers in macOS 26+ Calculator
                        // hook NSGestureRecognizer on the systemwide
                        // event tap; CGEventPostToPid bypasses that tap
                        // and the gesture never fires. Use the GLOBAL
                        // SessionEventTap (targetPid=null) here so the
                        // event reaches the OS hit-test pipeline.
                        // This DOES move the user's real cursor for the
                        // duration of the click — the cost of accuracy
                        // on these specific apps.
                        input!.Click(cx, cy, clickCount, mouseButton, targetPid: null);
                    }
                    finally { focusHandle?.Dispose(); }
                    return new CallToolResult { Content = [new TextContentBlock { Text = "ok (SwiftUI bypass, coordinate click)" }] };
                }
                catch (Exception coordEx)
                {
                    // Fall through to AX path if coordinate failed
                    System.Diagnostics.Debug.WriteLine($"PrefersCoord click failed: {coordEx.Message}");
                }
            }
        }

        try
        {
            // OCCU performAction repeats the verb clickCount times.
            // Right-click is handled differently — only AXShowMenu is
            // tried, never Press/Confirm/Open. We delegate to
            // TryInvokeAction("ShowMenu") for that case.
            if (mouseButton == MouseButton.Right)
            {
                if (element is Everywhere.Mac.Interop.AXUIElement axMac
                    && axMac.TryInvokeAction("showmenu"))
                {
                    return new CallToolResult { Content = [new TextContentBlock { Text = "ok (right-click → ShowMenu)" }] };
                }
                throw new InvalidOperationException("right-click requires AXShowMenu, which the element does not advertise");
            }
            element.Invoke(clickCount);
            return new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
        }
        catch (Exception axEx)
        {
            // OCCU-style coordinate fallback: if every AX verb in the chain
            // failed we still know the element's frame, so synthesize a real
            // click at its centre. Only safe when caller gave us the input
            // primitives — the MCP HTTP host always does, internal callers
            // (refactor seams) may not, in which case we surface the AX
            // failure verbatim.
            if (input is not null)
            {
                var rect = element.BoundingRectangle;
                if (rect.Width > 0 && rect.Height > 0)
                {
                    try
                    {
                        var cx = rect.X + rect.Width / 2.0;
                        var cy = rect.Y + rect.Height / 2.0;
                        // Best-effort focus: AX invoke didn't need the
                        // target foreground, but a real CGEvent click
                        // does — otherwise it lands on whichever app
                        // currently has focus.
                        IDisposable? focusHandle = null;
                        AppResolver.ResolvedApp? resolved = null;
                        if (focusBorrow is not null && context is not null && !string.IsNullOrEmpty(appHint))
                        {
                            resolved = AppResolver.Resolve(context, appHint);
                            if (resolved is not null)
                            {
                                focusHandle = focusBorrow.Acquire(
                                    resolved.Value.Window.NativeWindowHandle,
                                    requireFocus: true,
                                    processId: resolved.Value.ProcessId);
                            }
                        }
                        try
                        {
                            input.Click(cx, cy, clickCount, mouseButton,
                                targetPid: resolved?.ProcessId);
                        }
                        finally
                        {
                            focusHandle?.Dispose();
                        }
                        return new CallToolResult
                        {
                            Content = [new TextContentBlock
                            {
                                Text = $"ok (AX action chain failed: {axEx.Message}; coordinate fallback at {cx:0},{cy:0} succeeded)",
                            }],
                        };
                    }
                    catch (Exception coordEx)
                    {
                        return ToolErrors.Error(
                            $"AX action chain failed ({axEx.Message}) and coordinate fallback failed ({coordEx.Message}).");
                    }
                }
            }
            return ToolErrors.FromException(axEx, "invoke element");
        }
    }
}
