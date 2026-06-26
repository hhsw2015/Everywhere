using ModelContextProtocol.Protocol;

namespace Everywhere.Mcp.Tools;

/// <summary>
/// Helpers for emitting upstream-shaped tool-call errors. Upstream returns
/// <c>{ isError:true, content:[{type:"text", text:"…"}] }</c>; matching this
/// keeps existing client-side error parsers working byte-for-byte.
/// </summary>
internal static class ToolErrors
{
    public static CallToolResult Error(string message) =>
        new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = message }],
        };

    public static CallToolResult AppNotRunning(string app) =>
        Error($"App '{app}' not running. Call list_apps.");

    public static CallToolResult ElementIndexExpired(int index) =>
        Error($"Element index {index} not found in current snapshot.");

    public static CallToolResult NoFocusedApp() =>
        Error("No foreground application detected.");

    public static CallToolResult ParameterRequired(string name) =>
        Error($"Required parameter '{name}' missing.");

    /// <summary>
    /// Tool requires the OCCU AX backend (libAxHelper.dylib) but it is not
    /// registered. macOS builds register it on launch; EVERYWHERE_USE_OCCU=0
    /// disables registration as a kill switch. Windows/Linux currently have
    /// NO equivalent backend — automation tools are macOS-only for now (the
    /// vendored OpenComputerUseKit Swift library targets .macOS(.v14)).
    /// </summary>
    public static CallToolResult OccuRequired(string toolName)
    {
        if (OperatingSystem.IsMacOS())
        {
            return Error(
                $"{toolName}: OCCU AX backend not available. Ensure " +
                "EVERYWHERE_USE_OCCU is not set to 0 and that libAxHelper.dylib " +
                "is bundled in Contents/MonoBundle. This tool no longer has " +
                "a C# fallback path.");
        }

        return Error(
            $"{toolName}: native UI automation is only available on macOS " +
            "in this build. The OpenComputerUseKit Swift backend that powers " +
            "this tool family does not yet have a Windows/Linux equivalent. " +
            "Perception tools (pick_element, get_selected_text, get_focused_context, " +
            "get_clipboard, get_browser_url, get_browser_tabs, screenshot, ...) " +
            "still work cross-platform.");
    }

    /// <summary>
    /// Wraps an unexpected exception into a clean tool error, hiding internal types and
    /// stack-shaped strings while preserving enough signal for the agent to route.
    /// </summary>
    public static CallToolResult FromException(Exception ex, string contextLabel)
    {
        if (ex is OperationCanceledException)
        {
            return Error($"{contextLabel}: cancelled.");
        }

        if (ex is NotSupportedException)
        {
            return Error($"{contextLabel}: not supported on this platform yet.");
        }

        if (ex is ArgumentException || ex is InvalidOperationException || ex is TimeoutException)
        {
            return Error($"{contextLabel}: {ex.Message}");
        }

        return Error($"{contextLabel}: internal error.");
    }
}
