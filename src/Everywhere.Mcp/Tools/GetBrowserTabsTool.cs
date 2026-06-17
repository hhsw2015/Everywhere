using System.ComponentModel;
using System.Text.Json;
using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class GetBrowserTabsTool
{
    [McpServerTool(Name = "get_browser_tabs", ReadOnly = true)]
    [Description(
        "Return all tabs of a browser app (Safari / Chrome / Arc / Brave / Edge / " +
        "Chromium / Vivaldi / Opera) as JSON {\"app\": str, \"tabs\": [{\"title\":str, " +
        "\"url\":str, \"active\":bool}]}. Pass app_hint to target a specific browser; " +
        "omit to use the foreground app. On macOS this requires the user to grant " +
        "Apple Events to that browser once.")]
    public static CallToolResult GetBrowserTabs(
        IVisualElementContext context,
        IBrowserTabsReader reader,
        string? app_hint = null)
    {
        try
        {
            string? appKey;
            if (!string.IsNullOrWhiteSpace(app_hint))
            {
                var resolved = AppResolver.Resolve(context, app_hint);
                if (resolved is null) return ToolErrors.AppNotRunning(app_hint);
                appKey = resolved.Value.AppKey;
            }
            else
            {
                var focused = context.FocusedElement;
                if (focused is null) return Json(new { app = (string?)null, tabs = Array.Empty<object>() });
                appKey = AppKey.FromProcessId(focused.ProcessId);
            }

            var tabs = reader.GetTabs(appKey);
            if (tabs is null)
            {
                return Json(new { app = appKey, tabs = Array.Empty<object>(), supported = false });
            }
            return Json(new
            {
                app = appKey,
                supported = true,
                tabs = tabs.Select(t => new { title = t.Title, url = t.Url, active = t.IsActive }).ToArray(),
            });
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "get_browser_tabs");
        }
    }

    private static CallToolResult Json(object payload) =>
        new()
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload) }],
        };
}
