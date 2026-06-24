using System.ComponentModel;
using Everywhere.Interop;
using Everywhere.Mcp.Input;
using Everywhere.Mcp.Snapshot;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class SetValueTool
{
    [McpServerTool(Name = "set_value")]
    [Description("Replace the textual value of an indexed editable element (text field, combo box, slider, etc.). Tries AX SetValue first; falls back to focus + simulated keystrokes when SetValue is refused (Stripe/Cloudflare/some Electron inputs reject scripted SetValue on security-sensitive fields).")]
    public static CallToolResult SetValue(
        string app,
        string element_index,
        string value,
        SessionStore sessions,
        IInputSimulator input,
        FocusBorrow focusBorrow,
        IVisualElementContext context)
    {
        if (string.IsNullOrEmpty(element_index)) return ToolErrors.ParameterRequired("element_index");
        if (value is null) return ToolErrors.ParameterRequired("value");

        var (error, element) = ElementResolver.Resolve(sessions, element_index, appHint: app);
        if (error is not null) return error;

        try
        {
            element!.SetText(value);
            return new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
        }
        catch (Exception axEx)
        {
            // OCCU canUseKeyboardTextFallback: if the element is text-shaped
            // (TextEdit / Document / ComboBox) and AX SetValue refused, try
            // focus + clear + type via real keyboard events. This is what
            // pages the user can actually edit by hand also expect.
            if (element!.Type is VisualElementType.TextEdit
                or VisualElementType.Document
                or VisualElementType.ComboBox)
            {
                try
                {
                    return TypeFallback(element!, value, input, focusBorrow, context, app, axEx);
                }
                catch (Exception kbEx)
                {
                    return ToolErrors.Error(
                        $"set_value AX path failed ({axEx.Message}); keyboard fallback also failed ({kbEx.Message}).");
                }
            }
            return ToolErrors.FromException(axEx, "set_value");
        }
    }

    private static CallToolResult TypeFallback(
        IVisualElement element,
        string value,
        IInputSimulator input,
        FocusBorrow focusBorrow,
        IVisualElementContext context,
        string appHint,
        Exception axEx)
    {
        // Try to put the element under the cursor + give the app focus,
        // then select-all + delete + type the new text. AX Invoke on a
        // textfield typically places caret; we run it then a real Cmd+A
        // / Backspace clear so the existing value is removed.
        var rect = element.BoundingRectangle;
        var cx = rect.X + rect.Width / 2.0;
        var cy = rect.Y + rect.Height / 2.0;

        var resolved = AppResolver.Resolve(context, appHint);
        IDisposable? focus = null;
        try
        {
            if (resolved is not null)
            {
                focus = focusBorrow.Acquire(
                    resolved.Value.Window.NativeWindowHandle,
                    requireFocus: true,
                    processId: resolved.Value.ProcessId);
            }
            if (rect.Width > 0 && rect.Height > 0)
            {
                input.Click(cx, cy, 1, MouseButton.Left);
            }
            // Select-all + delete clears whatever is there. xdotool-style
            // names — on Mac the simulator maps super→Cmd, on Linux it's
            // literally super, on Windows it maps to ctrl. Same intent
            // ("select all + erase + type").
            input.PressKey(OperatingSystem.IsMacOS() ? "super+a" : "ctrl+a");
            input.PressKey("BackSpace");
            input.TypeText(value);
        }
        finally
        {
            focus?.Dispose();
        }
        return new CallToolResult
        {
            Content = [new TextContentBlock
            {
                Text = $"ok (AX SetValue refused: {axEx.Message}; keyboard fallback succeeded)",
            }],
        };
    }
}
