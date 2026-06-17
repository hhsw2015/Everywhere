using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class PressKeyTool
{
    [McpServerTool(Name = "press_key")]
    [Description("Press a key or key combination using xdotool-style names (e.g. 'a', 'Return', 'Tab', 'super+c', 'KP_0'). Brings the target window to the foreground first.")]
    public static CallToolResult PressKey(string app, string key) =>
        ToolErrors.Error("press_key is not yet supported in this build (Phase 4).");
}
