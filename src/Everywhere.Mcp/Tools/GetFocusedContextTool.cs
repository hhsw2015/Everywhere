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
    [Description("Get a single rich snapshot of whatever the user is currently looking at: focused window, selected text, accessibility tree with budget-bounded pruning, and screenshot. PREFER THIS over list_apps + get_app_state when the user uses deictic references (\"this\", \"that\", \"the error\", \"this code\", \"这个\"). Cheaper and faster than the two-step flow.")]
    public static async Task<CallToolResult> GetFocusedContext(
        int? budget,
        IVisualElementContext context,
        SessionStore sessions,
        CancellationToken cancellationToken)
    {
        var focused = context.FocusedElement;
        if (focused is null)
        {
            return ToolErrors.NoFocusedApp();
        }

        var topLevel = WalkToTopLevel(focused) ?? focused;
        var nodeBudget = Math.Clamp(budget ?? 4000, 1, UpstreamConstants.AccessibilityTreeMaxNodeCount * 4);

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
            // ponytail: tree text is the load-bearing field; screenshot best-effort.
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

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result) }],
        };
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
