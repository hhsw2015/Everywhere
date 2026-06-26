using System.ComponentModel;
using Everywhere.Interop;
using Everywhere.Mcp.Input;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class TypeTextTool
{
    [McpServerTool(Name = "type_text")]
    [Description("Type literal text into the focused control of the named app via simulated keystrokes. Brings the target window to the foreground first.")]
    public static CallToolResult TypeText(
        string app,
        string text,
        IInputSimulator input,
        FocusBorrow focusBorrow,
        IVisualElementContext context,
        Everywhere.Mcp.CursorOverlay.ITargetWindowHighlighter highlighter,
        IAxBridgeBackend? backend = null)
    {
        if (text is null) return ToolErrors.ParameterRequired("text");
        if (text.Length > 100_000) return ToolErrors.Error("text exceeds 100 000 character limit.");

        if (backend is not null)
        {
            var (txt, isError) = backend.TypeText(app, text);
            return isError ? ToolErrors.Error(txt) : new CallToolResult { Content = [new TextContentBlock { Text = txt }] };
        }

        var resolved = AppResolver.Resolve(context, app);
        if (resolved is null) return ToolErrors.AppNotRunning(app);

        try
        {
            using var _ = focusBorrow.Acquire(
                resolved.Value.Window.NativeWindowHandle,
                requireFocus: true,
                processId: resolved.Value.ProcessId);
            highlighter.Highlight(resolved.Value.Window.BoundingRectangle, $"Everywhere · {app}");
            input.TypeText(text, targetPid: resolved.Value.ProcessId);
            return new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "type_text");
        }
    }
}
