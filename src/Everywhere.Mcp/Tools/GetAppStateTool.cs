using System.ComponentModel;
using Everywhere.Interop;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class GetAppStateTool
{
    [McpServerTool(Name = "get_app_state", ReadOnly = true)]
    [Description(
        "Snapshot a NAMED app's largest visible window: indexed a11y tree. " +
        "Each tree row is prefixed [<element_index>]; pass that index to click/scroll/set_value/" +
        "perform_secondary_action. Issues a fresh index map; previously vended indices for this " +
        "app become invalid. " +
        "PREFER get_app_context(app_hint) when the app match is fuzzy — it does the matching for you.")]
    public static CallToolResult GetAppState(
        string app,
        IServiceProvider services,
        bool show_full_text = false)
    {
        if (services.GetService(typeof(IAxBridgeBackend)) is not IAxBridgeBackend backend)
            return ToolErrors.OccuRequired("get_app_state");

        var (text, isError) = backend.GetAppState(app, show_full_text);
        return isError
            ? ToolErrors.Error(text)
            : new CallToolResult { Content = [new TextContentBlock { Text = text }] };
    }
}
