using System.ComponentModel;
using Everywhere.Interop;
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
        double to_y,
        IServiceProvider services)
    {
        if (services.GetService(typeof(IAxBridgeBackend)) is not IAxBridgeBackend backend)
            return ToolErrors.OccuRequired("drag");

        var (text, isError) = backend.Drag(app, from_x, from_y, to_x, to_y);
        return isError
            ? ToolErrors.Error(text)
            : new CallToolResult { Content = [new TextContentBlock { Text = text }] };
    }
}
