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
        "Chromium / Vivaldi / Opera) as JSON. Pass app_hint to target a specific browser; " +
        "omit to use the foreground app. " +
        "Status field: \"ok\" / \"not_supported\" (app isn't a browser) / " +
        "\"permission_denied\" (Apple Events not granted yet — first call typically triggers " +
        "the macOS permission prompt; if user dismissed it once, re-grant via " +
        "System Settings → Privacy & Security → Automation).")]
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
                if (focused is null)
                    return Json(new { app = (string?)null, status = "no_focus", tabs = Array.Empty<object>() });
                appKey = AppKey.FromProcessId(focused.ProcessId);
            }

            var result = reader.GetTabs(appKey);
            var statusStr = result.Status switch
            {
                BrowserTabsStatus.Ok => "ok",
                BrowserTabsStatus.PermissionDenied => "permission_denied",
                _ => "not_supported",
            };

            return Json(new
            {
                app = appKey,
                status = statusStr,
                error = result.ErrorMessage,
                tabs = result.Tabs.Select(t => new { title = t.Title, url = t.Url, active = t.IsActive }).ToArray(),
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
            Content = [new TextContentBlock
            {
                Text = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                }),
            }],
        };
}
