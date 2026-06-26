using System.ComponentModel;
using Everywhere.Interop;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class PerformSecondaryActionTool
{
    [McpServerTool(Name = "perform_secondary_action")]
    [Description(
        "Invoke a named accessibility action on an indexed element. Pass any AXAction the " +
        "element exposes (e.g. \"AXPress\", \"AXShowMenu\", \"AXIncrement\", \"AXRaise\"). " +
        "Use get_app_state and inspect the element's \"Secondary Actions:\" list for valid " +
        "values. Common shortcuts: \"press\" / \"click\" → AXPress; \"context_menu\" / " +
        "\"right_click\" → AXShowMenu (the OCCU backend resolves these aliases).")]
    public static CallToolResult PerformSecondaryAction(
        string app,
        string element_index,
        string action,
        IServiceProvider services)
    {
        if (string.IsNullOrEmpty(element_index)) return ToolErrors.ParameterRequired("element_index");
        if (string.IsNullOrEmpty(action)) return ToolErrors.ParameterRequired("action");

        if (services.GetService(typeof(IAxBridgeBackend)) is not IAxBridgeBackend backend)
            return ToolErrors.OccuRequired("perform_secondary_action");

        var (text, isError) = backend.PerformSecondaryAction(app, element_index, action);
        return isError
            ? ToolErrors.Error(text)
            : new CallToolResult { Content = [new TextContentBlock { Text = text }] };
    }
}
