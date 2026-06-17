using System.ComponentModel;
using Everywhere.Mcp.Snapshot;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class PerformSecondaryActionTool
{
    [McpServerTool(Name = "perform_secondary_action")]
    [Description("Invoke a secondary accessibility action exposed by an indexed element. The set of supported actions varies per platform/control; the most common is 'press' (handled by click element-path).")]
    public static CallToolResult PerformSecondaryAction(
        string app,
        string element_index,
        string action,
        SessionStore sessions)
    {
        if (string.IsNullOrEmpty(element_index)) return ToolErrors.ParameterRequired("element_index");
        if (string.IsNullOrEmpty(action)) return ToolErrors.ParameterRequired("action");

        var (error, element) = ElementResolver.Resolve(sessions, element_index);
        if (error is not null) return error;

        try
        {
            // ponytail: the IVisualElement abstraction exposes Invoke as the canonical action;
            // platform-specific secondary actions land in Phase 4 alongside the input simulator.
            element!.Invoke();
            return new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
        }
        catch (Exception ex)
        {
            return ToolErrors.Error($"Failed to perform action '{action}': {ex.Message}");
        }
    }
}
