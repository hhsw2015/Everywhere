using System.ComponentModel;
using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class ClickTool
{
    [McpServerTool(Name = "click")]
    [Description("Click on a UI element. Pass element_index from a prior get_app_state when the target is in the indexed tree (no pointer movement, target window need not be foreground). Pass x/y screen pixel coordinates for free-form clicks (the target window will be brought to the foreground first). click_count defaults to 1; mouse_button defaults to left.")]
    public static CallToolResult Click(
        string app,
        string? element_index,
        double? x,
        double? y,
        int? click_count,
        string? mouse_button,
        SessionStore sessions)
    {
        if (!string.IsNullOrEmpty(element_index))
        {
            var (error, element) = ElementResolver.Resolve(sessions, element_index);
            if (error is not null)
            {
                return error;
            }

            try
            {
                element!.Invoke();
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = "ok" }],
                };
            }
            catch (Exception ex)
            {
                return ToolErrors.Error($"Failed to invoke element: {ex.Message}");
            }
        }

        if (x.HasValue && y.HasValue)
        {
            // Phase 4 will wire IInputSimulator + FocusBorrow.
            return ToolErrors.Error("Coordinate-based click is not yet supported in this build (Phase 4).");
        }

        return ToolErrors.Error("click requires either element_index or both x and y.");
    }
}
