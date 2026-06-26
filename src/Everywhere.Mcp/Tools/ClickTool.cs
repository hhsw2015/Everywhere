using System.ComponentModel;
using Everywhere.Interop;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class ClickTool
{
    [McpServerTool(Name = "click")]
    [Description("Click a UI element. Pass element_index from a prior get_app_state when the target is in the indexed tree (no pointer movement, target window need not be foreground). Pass x/y screen pixel coordinates for free-form clicks. click_count defaults to 1; mouse_button defaults to left.")]
    public static CallToolResult Click(
        string app,
        IServiceProvider services,
        string? element_index = null,
        double? x = null,
        double? y = null,
        int? click_count = null,
        string? mouse_button = null)
    {
        if (services.GetService(typeof(IAxBridgeBackend)) is not IAxBridgeBackend backend)
            return ToolErrors.OccuRequired("click");

        var hasIdx = !string.IsNullOrEmpty(element_index);
        var hasXY = x.HasValue && y.HasValue;
        if (!hasIdx && !hasXY)
            return ToolErrors.Error("click requires either element_index or both x and y.");

        var btn = (mouse_button ?? "left").ToLowerInvariant();
        var cc = click_count is { } cnt && cnt > 0 ? cnt : 1;
        var (text, isError) = backend.Click(app, element_index, x ?? 0, y ?? 0, hasXY && !hasIdx, cc, btn);
        return isError
            ? ToolErrors.Error(text)
            : new CallToolResult { Content = [new TextContentBlock { Text = text }] };
    }
}
