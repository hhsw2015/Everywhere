using System.ComponentModel;
using System.Text.Json;
using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using Everywhere.Mcp.Tools.Schemas;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class GetAppContextTool
{
    [McpServerTool(Name = "get_app_context", ReadOnly = true)]
    [Description(
        "Resolve a fuzzy app name (\"the browser\", \"slack\", \"vscode\", \"arc\") to its current " +
        "window state in ONE call. Internally: list_apps → fuzzy match → snapshot largest visible " +
        "window. PREFER this over calling list_apps + get_app_state separately. Returns the same " +
        "shape as get_app_state plus a 'matched' field describing which app and how confident.")]
    public static async Task<CallToolResult> GetAppContext(
        [Description("Fuzzy app name. Matched against process name AND window title (case-insensitive substring).")] string app_hint,
        bool show_full_text,
        IVisualElementContext context,
        SessionStore sessions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(app_hint)) return ToolErrors.ParameterRequired("app_hint");

        var resolved = AppResolver.Resolve(context, app_hint);
        if (resolved is null) return ToolErrors.AppNotRunning(app_hint);

        var window = resolved.Value.Window;
        var nodes = ElementIndexer.Walk(window);
        var elementMap = ElementIndexer.ToIndexMap(nodes);
        sessions.Issue(resolved.Value.AppKey, elementMap, window.NativeWindowHandle);

        var treeText = SnapshotRenderer.Render(nodes, show_full_text);

        string? screenshotBase64 = null;
        try
        {
            using var captured = await window.CaptureAsync(cancellationToken);
            screenshotBase64 = ScreenshotEncoder.EncodePngBase64(captured);
        }
        catch
        {
            // ponytail: best-effort screenshot; tree text is the load-bearing signal.
        }

        var bounds = window.BoundingRectangle;
        var payload = new
        {
            matched = new
            {
                app = resolved.Value.AppKey,
                window_title = window.Name,
                hint = app_hint,
            },
            app = resolved.Value.AppKey,
            window_title = window.Name,
            window_bounds = new WindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            screenshot_png_b64 = screenshotBase64,
            tree_text = treeText,
            tree_json = TreeJsonBuilder.Build(nodes),
        };

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload) }],
        };
    }
}
