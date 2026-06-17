using Everywhere.Mcp.Snapshot;

namespace Everywhere.Mac.Mcp;

/// <summary>
/// macOS Finder reader via AppleScript. Returns POSIX paths of every selected
/// item plus the active Finder window's target folder.
/// </summary>
public sealed class MacFinderReader(IAppleScriptRunner runner) : IFinderReader
{
    private const string Marker = "<<<FINDER_END>>>";

    private static readonly string Source =
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
            return out & """ + Marker + @""" & linefeed & fp
        end tell";

    public FinderSelection? GetSelection()
    {
        var raw = runner.Run(Source);
        if (string.IsNullOrEmpty(raw)) return null;

        var idx = raw.IndexOf(Marker, StringComparison.Ordinal);
        string selBlock;
        string? folder = null;
        if (idx >= 0)
        {
            selBlock = raw[..idx];
            var folderBlock = raw[(idx + Marker.Length)..].Trim();
            if (!string.IsNullOrEmpty(folderBlock)) folder = folderBlock;
        }
        else
        {
            selBlock = raw;
        }

        var files = new List<FinderItem>();
        foreach (var line in selBlock.Split('\n'))
        {
            var path = line.Trim();
            if (string.IsNullOrEmpty(path)) continue;
            // Treat anything that doesn't start with "/" as garbage (markers, errors, prompts).
            if (!path.StartsWith('/')) continue;
            var trimmed = path.TrimEnd('/');
            var name = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(name)) name = path;
            var isDir = path.EndsWith('/') || Directory.Exists(path);
            files.Add(new FinderItem(path, name, isDir));
        }

        return new FinderSelection(files, folder);
    }
}
