using System.ComponentModel;
using System.Text.Json;
using Everywhere.Interop;
using Everywhere.Mcp.Input;
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
        "shape as get_app_state plus a 'matched' field describing which app and how confident. " +
        "raise_if_needed: When true, briefly raise the target app to the foreground before reading, " +
        "then restore the previous foreground. Pass true ONLY when (a) a prior read returned an empty " +
        "or incomplete tree (Electron / Wayland apps lazy-load a11y in foreground only), (b) the user " +
        "explicitly asked to switch to and check the app, or (c) you also need a screenshot of an app " +
        "currently behind another window. DO NOT pass true for routine reads — most apps expose full " +
        "a11y in background and raising disrupts the user.")]
    public static async Task<CallToolResult> GetAppContext(
        [Description("Fuzzy app name. Matched against process name AND window title (case-insensitive substring).")] string app_hint,
        IVisualElementContext context,
        SessionStore sessions,
        FocusBorrow focusBorrow,
        CancellationToken cancellationToken,
        bool show_full_text = false,
        bool raise_if_needed = false,
        bool include_screenshot = false,
        bool include_tree_json = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(app_hint)) return ToolErrors.ParameterRequired("app_hint");

            var resolved = AppResolver.Resolve(context, app_hint);
            if (resolved is null) return ToolErrors.AppNotRunning(app_hint);

            using var borrow = raise_if_needed
                ? focusBorrow.Acquire(resolved.Value.Window.NativeWindowHandle, requireFocus: true, processId: resolved.Value.ProcessId)
                : null;

            var window = resolved.Value.Window;
            var nodes = ElementIndexer.Walk(window);
            var elementMap = ElementIndexer.ToIndexMap(nodes);
            sessions.Issue(resolved.Value.AppKey, elementMap, window.NativeWindowHandle);

            var treeText = SnapshotRenderer.Render(nodes, show_full_text);

            // ponytail: base64 screenshot dominates payload size when on
            // (~50KB-300KB). Default off; opt in per-call via
            // include_screenshot. Vision-capable callers can also hit the
            // dedicated `screenshot` tool which returns just the image.
            string? screenshotBase64 = null;
            if (include_screenshot)
            {
                try
                {
                    using var captured = await window.CaptureAsync(cancellationToken);
                    screenshotBase64 = ScreenshotEncoder.EncodeBase64(captured);
                }
                catch
                {
                    // best-effort screenshot.
                }
            }

            var bounds = window.BoundingRectangle;
            var inner = new AppStateResult
            {
                App = resolved.Value.AppKey,
                WindowTitle = window.Name,
                WindowBounds = new WindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height),
                ScreenshotPngBase64 = screenshotBase64,
                TreeText = SnapshotRenderer.AppendOccuFooter(
                    treeText,
                    context.FocusedElement?.GetSelectionText(),
                    context.FocusedElement?.Name),
                // ponytail: TreeJson duplicates TreeText, ~doubles payload.
                // Opt in per-call.
                TreeJson = include_tree_json ? TreeJsonBuilder.Build(nodes) : null,
                FocusedSummary = context.FocusedElement?.Name,
                SelectedText = context.FocusedElement?.GetSelectionText(),
            };
            SemanticEnricher.Apply(inner, nodes);

            var payload = new
            {
                matched = new
                {
                    app = resolved.Value.AppKey,
                    window_title = window.Name,
                    hint = app_hint,
                    raised = raise_if_needed,
                },
                app = inner.App,
                window_title = inner.WindowTitle,
                window_bounds = inner.WindowBounds,
                screenshot_png_b64 = inner.ScreenshotPngBase64,
                tree_text = inner.TreeText,
                tree_json = inner.TreeJson,
                selected_items = inner.SelectedItems,
                focused_items = inner.FocusedItems,
                focused_path = inner.FocusedPath,
            };

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload) }],
            };
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "get_app_context");
        }
    }
}
