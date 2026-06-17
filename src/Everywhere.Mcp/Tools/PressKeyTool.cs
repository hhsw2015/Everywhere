using System.ComponentModel;
using Everywhere.Interop;
using Everywhere.Mcp.Input;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class PressKeyTool
{
    [McpServerTool(Name = "press_key")]
    [Description("Press a key or key combination using xdotool-style names (e.g. 'a', 'Return', 'Tab', 'super+c', 'KP_0'). Brings the target window to the foreground first.")]
    public static CallToolResult PressKey(
        string app,
        string key,
        IInputSimulator input,
        FocusBorrow focusBorrow,
        IVisualElementContext context)
    {
        if (string.IsNullOrEmpty(key)) return ToolErrors.ParameterRequired("key");

        var resolved = AppResolver.Resolve(context, app);
        if (resolved is null) return ToolErrors.AppNotRunning(app);

        try
        {
            using var _ = focusBorrow.Acquire(resolved.Value.Window.NativeWindowHandle, requireFocus: true);
            input.PressKey(key);
            return new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
        }
        catch (Exception ex)
        {
            return ToolErrors.Error(ex.Message);
        }
    }
}
