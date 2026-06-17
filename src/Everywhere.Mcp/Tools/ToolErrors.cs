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
