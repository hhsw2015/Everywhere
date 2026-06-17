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
    [Description(
        "Snapshot a NAMED app's largest visible window: indexed a11y tree + screenshot. " +
        "Each tree row is prefixed [<element_index>]; pass that index to click/scroll/set_value/" +
        "perform_secondary_action. Issues a fresh index map; previously vended indices for this " +
        "app become invalid. " +
        "PREFER get_app_context(app_hint) when the app match is fuzzy — it does the matching for you.")]
    public static async Task<CallToolResult> GetAppState(
        string app,
        bool show_full_text,
        IVisualElementContext context,
        SessionStore sessions,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = AppResolver.Resolve(context, app);
            if (resolved is null) return ToolErrors.AppNotRunning(app);

            var window = resolved.Value.Window;
            var nodes = ElementIndexer.Walk(window);
            var elementMap = ElementIndexer.ToIndexMap(nodes);
            sessions.Issue(resolved.Value.AppKey, elementMap, window.NativeWindowHandle);

            var treeText = SnapshotRenderer.Render(nodes, show_full_text);

            string? screenshotBase64 = null;
            try
            {
                using var captured = await window.CaptureAsync(cancellationToken);
                screenshotBase64 = ScreenshotEncoder.EncodeBase64(captured);
            }
            catch
            {
                // best-effort screenshot; tree text is the load-bearing signal.
            }

            var bounds = window.BoundingRectangle;
            var result = new AppStateResult
            {
                App = resolved.Value.AppKey,
                WindowTitle = window.Name,
                WindowBounds = new WindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height),
                ScreenshotPngBase64 = screenshotBase64,
                TreeText = treeText,
                TreeJson = TreeJsonBuilder.Build(nodes),
                FocusedSummary = context.FocusedElement?.Name,
                SelectedText = context.FocusedElement?.GetSelectionText(),
            };
            SemanticEnricher.Apply(result, nodes);

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result) }],
            };
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "get_app_state");
        }
    }
}
