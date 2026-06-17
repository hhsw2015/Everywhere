using Everywhere.Mcp.Snapshot;

namespace Everywhere.Mac.Mcp;

/// <summary>
/// macOS Finder reader via AppleScript. Returns POSIX paths of every selected
/// item plus the active Finder window's target folder.
/// </summary>
public sealed class MacFinderReader(IAppleScriptRunner runner) : IFinderReader
{
    private const string Source =
        @"tell application ""Finder""
            set sel to selection
            set out to """"
            repeat with i in sel
                set out to out & POSIX path of (i as alias) & linefeed
            end repeat
            try
                set fp to POSIX path of ((target of front window) as alias)
            on error
                set fp to """"
            end try
            return out & ""---"" & linefeed & fp
        end tell";

    public FinderSelection? GetSelection()
    {
        var raw = runner.Run(Source);
        if (string.IsNullOrEmpty(raw)) return null;

        var separator = "\n---\n";
        var idx = raw.IndexOf(separator, StringComparison.Ordinal);
        var selBlock = idx >= 0 ? raw[..idx] : raw;
        var folder = idx >= 0 ? raw[(idx + separator.Length)..].Trim() : null;
        if (string.IsNullOrEmpty(folder)) folder = null;

        var files = new List<FinderItem>();
        foreach (var line in selBlock.Split('\n'))
        {
            var path = line.Trim();
            if (string.IsNullOrEmpty(path)) continue;
            var trimmed = path.TrimEnd('/');
            var name = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(name)) name = path;
            var isDir = path.EndsWith('/') || Directory.Exists(path);
            files.Add(new FinderItem(path, name, isDir));
        }

        return new FinderSelection(files, folder);
    }
}
