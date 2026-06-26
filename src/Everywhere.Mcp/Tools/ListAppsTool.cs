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
        "List every running app with at least one top-level window — including menubar-only " +
        "apps like Bartender/Typeless. Each entry has \"app\" (process key for the 'app' " +
        "parameter of other tools), \"title\" (the largest window's title), and \"process_id\". " +
        "PREFER get_app_context(app_hint) when the user names an app — it does list+match+snapshot " +
        "in one call.")]
    public static CallToolResult ListApps(IVisualElementContext context, IAxBridgeBackend? backend = null)
    {
        if (backend is not null)
        {
            var (text, isError) = backend.ListApps();
            return isError
                ? ToolErrors.Error(text)
                : new CallToolResult { Content = [new TextContentBlock { Text = text }] };
        }

        try
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
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "list_apps");
        }
    }
}
