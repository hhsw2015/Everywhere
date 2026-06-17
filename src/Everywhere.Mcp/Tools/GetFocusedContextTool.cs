using System.ComponentModel;
using System.Text.Json;
using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using Everywhere.Mcp.Tools.Schemas;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class GetFocusedContextTool
{
    [McpServerTool(Name = "get_focused_context", ReadOnly = true)]
    [Description(
        "Snapshot of the user's CURRENT foreground app — focused window + selected text + " +
        "indexed accessibility tree + screenshot. Use ONLY when the user references their " +
        "current view with deictic words: \"this\", \"that\", \"here\", \"the error\", " +
        "\"this code\", \"这个\". " +
        "DO NOT use when the user names a specific app (\"the browser\", \"Chrome\", \"VSCode\", " +
        "\"Slack\") — call get_app_context instead. " +
        "DO NOT use when the user has pre-pinned an element via the Pin Element hotkey — " +
        "call read_pick instead.")]
    public static async Task<CallToolResult> GetFocusedContext(
        IVisualElementContext context,
        SessionStore sessions,
        CancellationToken cancellationToken,
        int? budget = null)
    {
        try
        {
            var focused = context.FocusedElement;
            if (focused is null) return ToolErrors.NoFocusedApp();

            var topLevel = WalkToTopLevel(focused) ?? focused;
            // Default budget 4000; for app shells whose top-level is a Document (browsers,
            // PDF readers) bump to the cap so the agent doesn't see "omitted_node_count: 714"
            // on the very first call.
            var defaultBudget = focused.Type == VisualElementType.Document
                ? UpstreamConstants.AccessibilityTreeMaxNodeCount
                : 4000;
            var nodeBudget = Math.Clamp(budget ?? defaultBudget, 1, UpstreamConstants.AccessibilityTreeMaxNodeCount * 4);

            var nodes = ElementIndexer.Walk(topLevel, maxNodeCount: Math.Min(nodeBudget, UpstreamConstants.AccessibilityTreeMaxNodeCount));
            var elementMap = ElementIndexer.ToIndexMap(nodes);
            var appKey = AppKey.FromProcessId(topLevel.ProcessId);
            sessions.Issue(appKey, elementMap, topLevel.NativeWindowHandle);

            var totalDescendants = topLevel.GetDescendants(includeSelf: true).Count();
            var omitted = totalDescendants > nodes.Count;

            string? screenshot = null;
            try
            {
                using var captured = await topLevel.CaptureAsync(cancellationToken);
                screenshot = ScreenshotEncoder.EncodePngBase64(captured);
            }
            catch
            {
                // best-effort screenshot.
            }

            var bounds = topLevel.BoundingRectangle;
            var result = new FocusedContextResult
            {
                App = appKey,
                WindowTitle = topLevel.Name,
                WindowBounds = new WindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height),
                ScreenshotPngBase64 = screenshot,
                TreeText = SnapshotRenderer.Render(nodes, showFullText: false),
                FocusedSummary = focused.Name,
                SelectedText = focused.GetSelectionText(),
                OmittedChildren = omitted,
                OmittedNodeCount = Math.Max(0, totalDescendants - nodes.Count),
                TreeJson = TreeJsonBuilder.Build(nodes),
            };
            SemanticEnricher.Apply(result, nodes);

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result) }],
            };
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "get_focused_context");
        }
    }

    private static IVisualElement? WalkToTopLevel(IVisualElement element)
    {
        var current = element;
        while (current != null && current.Type != VisualElementType.TopLevel)
        {
            current = current.Parent;
        }
        return current;
    }
}
