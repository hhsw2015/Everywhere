using System.ComponentModel;
using System.Text.Json;
using Everywhere.Interop;
using Everywhere.Mcp.Tools.Schemas;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class ListAppsTool
{
    [McpServerTool(Name = "list_apps", ReadOnly = true)]
    [Description(
        "List all running apps that have a visible window the user could be looking at. " +
        "Each entry has \"app\" (process key for the 'app' parameter of other tools), " +
        "\"title\" (window title), and \"process_id\". Menubar widgets and headless agents " +
        "are filtered out. " +
        "PREFER get_app_context(app_hint) when the user names an app — it does list+match+snapshot " +
        "in one call.")]
    public static CallToolResult ListApps(IVisualElementContext context)
    {
        var apps = AppResolver.ListApps(context)
            .Select(a => new AppListItem
            {
                App = a.AppKey,
                Title = a.Window.Name,
                ProcessId = a.ProcessId,
            })
            .ToArray();

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(apps) }],
        };
    }
}
