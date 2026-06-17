using System.ComponentModel;
using System.Text.Json;
using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using Everywhere.Mcp.Tools.Schemas;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class GetAppStateTool
{
    [McpServerTool(Name = "get_app_state", ReadOnly = true)]
    [Description("Bring the named app to focus, capture a screenshot, and emit an indented accessibility tree text where each visible element is prefixed with [<element_index>] — those indices are the values you pass to click/scroll/set_value/perform_secondary_action. Issues a fresh element_index map; previously vended indices for this app become invalid.")]
    public static async Task<CallToolResult> GetAppState(
        string app,
        bool show_full_text,
        IVisualElementContext context,
        SessionStore sessions,
        CancellationToken cancellationToken)
    {
        var resolved = AppResolver.Resolve(context, app);
        if (resolved is null)
        {
            return ToolErrors.AppNotRunning(app);
        }

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
        var result = new AppStateResult
        {
            WindowTitle = window.Name,
            WindowBounds = new WindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            ScreenshotPngBase64 = screenshotBase64,
            TreeText = treeText,
            FocusedSummary = context.FocusedElement?.Name,
            SelectedText = context.FocusedElement?.GetSelectionText(),
        };

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result) }],
        };
    }
}
