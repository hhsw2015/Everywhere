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
        IVisualElementContext context)
    {
        if (text is null) return ToolErrors.ParameterRequired("text");
        if (text.Length > 100_000) return ToolErrors.Error("text exceeds 100 000 character limit.");

        var resolved = AppResolver.Resolve(context, app);
        if (resolved is null) return ToolErrors.AppNotRunning(app);

        try
        {
            using var _ = focusBorrow.Acquire(
                resolved.Value.Window.NativeWindowHandle,
                requireFocus: true,
                processId: resolved.Value.ProcessId);
            input.TypeText(text);
            return new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "type_text");
        }
    }
}
