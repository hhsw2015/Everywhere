using System.ComponentModel;
using Everywhere.Interop;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class ScrollTool
{
    private const int MaxPages = 100;

    [McpServerTool(Name = "scroll")]
    [Description("Scroll an indexed element by a number of pages. direction is 'up'|'down'|'left'|'right'; pages defaults to 1 (max 100, fractional values round to nearest int >=1). Uses CGEvent scroll wheel events on macOS (mirrors OCCU scrollTargeted/Globally).")]
    public static CallToolResult Scroll(
        string app,
        string element_index,
        string direction,
        IServiceProvider services,
        double? pages = null)
    {
        if (string.IsNullOrEmpty(element_index)) return ToolErrors.ParameterRequired("element_index");
        if (string.IsNullOrEmpty(direction)) return ToolErrors.ParameterRequired("direction");

        var dir = direction.ToLowerInvariant();
        if (dir is not ("up" or "down" or "left" or "right"))
            return ToolErrors.Error($"Invalid direction '{direction}'. Expected up|down|left|right.");

        var amount = pages ?? 1.0;
        if (double.IsNaN(amount) || amount <= 0 || amount > MaxPages)
            return ToolErrors.Error($"pages must be in (0, {MaxPages}].");

        if (services.GetService(typeof(IAxBridgeBackend)) is not IAxBridgeBackend backend)
            return ToolErrors.OccuRequired("scroll");

        var (txt, isError) = backend.Scroll(app, dir, element_index, amount);
        return isError
            ? ToolErrors.Error(txt)
            : new CallToolResult { Content = [new TextContentBlock { Text = txt }] };
    }
}
