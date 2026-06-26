using System.ComponentModel;
using Everywhere.Interop;
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
        IServiceProvider services)
    {
        if (string.IsNullOrEmpty(key)) return ToolErrors.ParameterRequired("key");

        if (services.GetService(typeof(IAxBridgeBackend)) is not IAxBridgeBackend backend)
            return ToolErrors.OccuRequired("press_key");

        var (txt, isError) = backend.PressKey(app, key);
        return isError
            ? ToolErrors.Error(txt)
            : new CallToolResult { Content = [new TextContentBlock { Text = txt }] };
    }
}
