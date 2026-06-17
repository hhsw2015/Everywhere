using System.ComponentModel;
using System.Text.Json;
using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class GetBrowserUrlTool
{
    [McpServerTool(Name = "get_browser_url", ReadOnly = true)]
    [Description(
        "Return the URL of the focused browser app's currently visible tab as JSON " +
        "{\"app\": string|null, \"url\": string|null}. " +
        "Pass app_hint to target a specific browser (e.g. \"arc\", \"chrome\", \"safari\"); " +
        "omit to query the foreground app. Returns url=null when the app isn't a known " +
        "browser or no URL is exposed via accessibility.")]
    public static CallToolResult GetBrowserUrl(
        IVisualElementContext context,
        IBrowserUrlReader reader,
        string? app_hint = null)
    {
        try
        {
            int pid;
            string? appKey;

            if (!string.IsNullOrWhiteSpace(app_hint))
            {
                var resolved = AppResolver.Resolve(context, app_hint);
                if (resolved is null) return ToolErrors.AppNotRunning(app_hint);
                pid = resolved.Value.ProcessId;
                appKey = resolved.Value.AppKey;
            }
            else
            {
                var focused = context.FocusedElement;
                if (focused is null)
                {
                    return Json(new { app = (string?)null, url = (string?)null });
                }
                pid = focused.ProcessId;
                appKey = AppKey.FromProcessId(pid);
            }

            var url = reader.GetUrl(pid);
            return Json(new { app = appKey, url });
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "get_browser_url");
        }
    }

    private static CallToolResult Json(object payload) =>
        new()
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload) }],
        };
}
