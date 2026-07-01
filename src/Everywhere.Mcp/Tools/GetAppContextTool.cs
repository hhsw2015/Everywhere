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
        "Fuzzy app name → window state (indexed a11y tree). Combines list_apps + get_app_state; " +
        "prefer this over calling them separately. raise_if_needed=true only when a prior read " +
        "returned an incomplete tree (Electron/Wayland lazy-load a11y in foreground) or you need " +
        "a screenshot of an occluded window — raising interrupts the user.")]
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

            void LogPhase(string phase, long t0)
            {
                var ms = System.Environment.TickCount64 - t0;
                try
                {
                    System.IO.File.AppendAllText("/tmp/everywhere-perf.log",
                        $"[{System.DateTime.Now:HH:mm:ss.fff}] get_app_context({app_hint}) {phase} took {ms}ms\n");
                }
                catch { }
            }

            var t0 = System.Environment.TickCount64;
            var t = t0;
            var resolved = AppResolver.Resolve(context, app_hint);
            LogPhase("Resolve", t); t = System.Environment.TickCount64;
            if (resolved is null) return ToolErrors.AppNotRunning(app_hint);

            using var borrow = raise_if_needed
                ? focusBorrow.Acquire(resolved.Value.Window.NativeWindowHandle, requireFocus: true, processId: resolved.Value.ProcessId)
                : null;
            LogPhase("FocusBorrow.Acquire", t); t = System.Environment.TickCount64;

            var window = resolved.Value.Window;
            var nodes = ElementIndexer.Walk(window);
            LogPhase($"Walk(nodes={nodes.Count})", t); t = System.Environment.TickCount64;
            var elementMap = ElementIndexer.ToIndexMap(nodes);
            sessions.Issue(resolved.Value.AppKey, elementMap, window.NativeWindowHandle);

            var treeText = SnapshotRenderer.Render(nodes, show_full_text);
            LogPhase("Render", t);
            LogPhase("TOTAL", t0);

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
