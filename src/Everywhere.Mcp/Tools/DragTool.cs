using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class DragTool
{
    [McpServerTool(Name = "drag")]
    [Description("Press at (from_x, from_y), drag to (to_x, to_y), then release. Coordinates are screen pixels; the target window will be brought to the foreground first.")]
    public static CallToolResult Drag(
        string app,
        double from_x,
        double from_y,
        double to_x,
        double to_y) =>
        ToolErrors.Error("drag is not yet supported in this build (Phase 4).");
}
