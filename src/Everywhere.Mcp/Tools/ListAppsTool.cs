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
    [Description("List all currently running, user-visible applications. Returns the values you can pass as the 'app' argument to other Computer Use tools.")]
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
