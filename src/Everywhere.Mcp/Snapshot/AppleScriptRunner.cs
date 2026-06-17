namespace Everywhere.Mcp.Snapshot;

public enum AppleScriptStatus
{
    /// <summary>Script returned 0 with output (possibly empty).</summary>
    Ok,
    /// <summary>osascript not present / spawn failed / timed out / general error.</summary>
    Failed,
    /// <summary>App refused Apple Events (TCC permission denied) — distinct from generic failure.</summary>
    PermissionDenied,
    /// <summary>The runner itself isn't implemented for this platform (Null fallback).</summary>
    NotSupported,
}

public sealed record AppleScriptResult(
    AppleScriptStatus Status,
    string? Output,
    string? ErrorMessage = null);

/// <summary>
/// Runs an AppleScript snippet and returns the outcome. Result struct distinguishes
/// "no data" from "permission denied" from "platform unsupported" so callers can
/// produce actionable agent-facing errors. macOS impl shells out to /usr/bin/osascript.
/// </summary>
public interface IAppleScriptRunner
{
    AppleScriptResult Run(string source);
}

internal sealed class NullAppleScriptRunner : IAppleScriptRunner
{
    public AppleScriptResult Run(string source) =>
        new(AppleScriptStatus.NotSupported, null, "AppleScript not supported on this platform.");
}
