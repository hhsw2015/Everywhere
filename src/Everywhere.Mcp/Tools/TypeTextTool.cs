using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class TypeTextTool
{
    [McpServerTool(Name = "type_text")]
    [Description("Type literal text into the focused control of the named app via simulated keystrokes. Brings the target window to the foreground first.")]
    public static CallToolResult TypeText(string app, string text) =>
        ToolErrors.Error("type_text is not yet supported in this build (Phase 4).");
}
