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
        string? appHint = null)
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

        try
        {
            element.Invoke();
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
                            input.Click(cx, cy, 1, MouseButton.Left,
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
