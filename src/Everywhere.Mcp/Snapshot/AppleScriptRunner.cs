namespace Everywhere.Mcp.Snapshot;

/// <summary>
/// Runs an AppleScript snippet and returns stdout as a single string, or null on
/// failure. Bridge for tools that need data Apple only exposes via scripting
/// (Finder selection, browser tab lists). macOS implementation uses NSAppleScript;
/// non-macOS impls return null.
/// </summary>
public interface IAppleScriptRunner
{
    string? Run(string source);
}

internal sealed class NullAppleScriptRunner : IAppleScriptRunner
{
    public string? Run(string source) => null;
}
