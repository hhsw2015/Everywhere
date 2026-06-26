using System.ComponentModel;
using Everywhere.Interop;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class ListAppsTool
{
    [McpServerTool(Name = "list_apps", ReadOnly = true)]
    [Description(
        "List every running app with at least one top-level window — including menubar-only " +
        "apps like Bartender/Typeless. Each entry has \"app\" (process key for the 'app' " +
        "parameter of other tools), \"title\" (the largest window's title), and \"process_id\". " +
        "PREFER get_app_context(app_hint) when the user names an app — it does list+match+snapshot " +
        "in one call.")]
    public static CallToolResult ListApps(IServiceProvider services)
    {
        if (services.GetService(typeof(IAxBridgeBackend)) is not IAxBridgeBackend backend)
            return ToolErrors.OccuRequired("list_apps");

        var (text, isError) = backend.ListApps();
        return isError
            ? ToolErrors.Error(text)
            : new CallToolResult { Content = [new TextContentBlock { Text = text }] };
    }
}
