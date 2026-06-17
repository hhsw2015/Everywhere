namespace Everywhere.Mcp.Snapshot;

/// <summary>
/// Single source of truth for the on-disk path of the context stash file the
/// Snapshot-Context hotkey writes and the Claude Code UserPromptSubmit hook
/// reads. The Rust hook (<c>tools/everywhere-context-hook/src/main.rs</c>) MUST
/// agree with what's computed here for each OS.
/// </summary>
public static class StashPaths
{
    public const string FileName = "context-stash.json";

    public static string ContextStash()
    {
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                "Everywhere",
                FileName);
        }
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Everywhere",
                FileName);
        }
        // Linux / freedesktop XDG: prefer $XDG_DATA_HOME, fall back to ~/.local/share.
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(xdg))
        {
            xdg = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share");
        }
        return Path.Combine(xdg, "Everywhere", FileName);
    }
}
