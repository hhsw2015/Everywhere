using Everywhere.Mcp.Snapshot;

namespace Everywhere.Mac.Mcp;

/// <summary>
/// macOS Finder reader via AppleScript. Returns POSIX paths of every selected
/// item plus the active Finder window's target folder. Uses ASCII control
/// characters as separators (NUL between paths, RS before the folder block) so
/// filenames containing newlines aren't fragmented.
/// </summary>
public sealed class MacFinderReader(IAppleScriptRunner runner) : IFinderReader
{
    private const string Source =
        @"tell application ""Finder""
            set NUL to (ASCII character 0)
            set RS to (ASCII character 30)
            set sel to selection
            set out to """"
            repeat with i in sel
                set out to out & POSIX path of (i as alias) & NUL
            end repeat
            try
                set fp to POSIX path of ((target of front window) as alias)
            on error
                set fp to """"
            end try
            return out & RS & fp
        end tell";

    public FinderResult GetSelection()
    {
        var ar = runner.Run(Source);
        switch (ar.Status)
        {
            case AppleScriptStatus.NotSupported:
                return new FinderResult(FinderStatus.NotSupported, null, ar.ErrorMessage);
            case AppleScriptStatus.PermissionDenied:
                return new FinderResult(FinderStatus.PermissionDenied, null, ar.ErrorMessage);
            case AppleScriptStatus.Failed:
                return new FinderResult(FinderStatus.PermissionDenied, null, ar.ErrorMessage);
        }

        var raw = ar.Output ?? string.Empty;
        var rsIdx = raw.IndexOf('\x1E');
        var selBlock = rsIdx >= 0 ? raw[..rsIdx] : raw;
        var folder = rsIdx >= 0 ? raw[(rsIdx + 1)..].Trim() : null;
        if (string.IsNullOrEmpty(folder)) folder = null;

        var files = new List<FinderItem>();
        foreach (var entry in selBlock.Split('\0'))
        {
            var path = entry.TrimEnd('\r').TrimEnd('\n');
            if (string.IsNullOrEmpty(path)) continue;
            if (!path.StartsWith('/')) continue;

            var isDir = path.EndsWith('/');
            var canonical = isDir ? path.TrimEnd('/') : path;
            var name = System.IO.Path.GetFileName(canonical);
            if (string.IsNullOrEmpty(name)) name = path;

            if (!isDir)
            {
                try { isDir = Directory.Exists(path); } catch { /* leave false */ }
            }

            files.Add(new FinderItem(path, name, isDir));
        }

        return new FinderResult(FinderStatus.Ok, new FinderSelection(files, folder));
    }
}
