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
        "Snapshot user's CURRENT foreground app — window + selected text + a11y tree + screenshot. " +
        "Use for deictic \"this/that/here/这个\". " +
        "DO NOT use when a specific app is named (call get_app_context) or when user has pinned via " +
        "Pin-Element hotkey (call read_pick).")]
    public static async Task<CallToolResult> GetFocusedContext(
        IVisualElementContext context,
        SessionStore sessions,
        CancellationToken cancellationToken,
        int? budget = null,
        bool include_screenshot = false,
        bool include_tree_json = false)
    {
        try
        {
            var focused = context.FocusedElement;
            if (focused is null) return ToolErrors.NoFocusedApp();

            var topLevel = WalkToTopLevel(focused) ?? focused;
            // Default budget 4000; bump to the cap when the focused element OR any of its
            // ancestors is a Document — covers browsers / PDF readers / Notion-style canvases
            // where the focused leaf is usually a TextEdit/Hyperlink inside a Document.
            var hasDocumentAncestor = HasDocumentInChain(focused);
            var defaultBudget = hasDocumentAncestor
                ? UpstreamConstants.AccessibilityTreeMaxNodeCount
                : 4000;
            var nodeBudget = Math.Clamp(budget ?? defaultBudget, 1, UpstreamConstants.AccessibilityTreeMaxNodeCount * 4);

            var nodes = ElementIndexer.Walk(topLevel, maxNodeCount: Math.Min(nodeBudget, UpstreamConstants.AccessibilityTreeMaxNodeCount));
            var elementMap = ElementIndexer.ToIndexMap(nodes);
            var appKey = AppKey.FromProcessId(topLevel.ProcessId);
            sessions.Issue(appKey, elementMap, topLevel.NativeWindowHandle);

            var totalDescendants = topLevel.GetDescendants(includeSelf: true).Count();
            var omitted = totalDescendants > nodes.Count;

            // ponytail: see GetAppContextTool. Default off, opt in per call.
            string? screenshot = null;
            if (include_screenshot)
            {
                try
                {
                    using var captured = await topLevel.CaptureAsync(cancellationToken);
                    screenshot = ScreenshotEncoder.EncodeBase64(captured);
                }
                catch
                {
                    // best-effort screenshot.
                }
            }

            var bounds = topLevel.BoundingRectangle;
            var result = new FocusedContextResult
            {
                App = appKey,
                WindowTitle = topLevel.Name,
                WindowBounds = new WindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height),
                ScreenshotPngBase64 = screenshot,
                TreeText = SnapshotRenderer.AppendOccuFooter(
                    SnapshotRenderer.Render(nodes, showFullText: false),
                    focused.GetSelectionText(),
                    focused.Name),
                FocusedSummary = focused.Name,
                SelectedText = focused.GetSelectionText(),
                OmittedChildren = omitted,
                OmittedNodeCount = Math.Max(0, totalDescendants - nodes.Count),
                // ponytail: TreeJson duplicates TreeText. Default omit, opt in per call.
                TreeJson = include_tree_json ? TreeJsonBuilder.Build(nodes) : null,
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

    private static bool HasDocumentInChain(IVisualElement element)
    {
        var cur = element;
        for (var i = 0; cur != null && i < 32; i++)
        {
            if (cur.Type == VisualElementType.Document) return true;
            cur = cur.Parent;
        }
        return false;
    }
}
