using Everywhere.Mcp.Snapshot;

namespace Everywhere.Mac.Mcp;

/// <summary>
/// macOS browser-tabs reader. Per-app AppleScript: Safari / Chrome (and Chromium
/// derivatives like Arc/Brave/Edge with the same dictionary). Format per line:
/// <c>active|title|url</c> — caller parses.
/// </summary>
public sealed class MacBrowserTabsReader(IAppleScriptRunner runner) : IBrowserTabsReader
{
    public IReadOnlyList<BrowserTab>? GetTabs(string appKey)
    {
        if (string.IsNullOrWhiteSpace(appKey)) return null;
        var lower = appKey.ToLowerInvariant();
        var script = ScriptFor(lower);
        if (script is null) return null;

        var raw = runner.Run(script);
        if (string.IsNullOrEmpty(raw)) return null;

        var tabs = new List<BrowserTab>();
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            var parts = trimmed.Split('\t', 3);
            if (parts.Length < 3) continue;
            tabs.Add(new BrowserTab(
                Title: parts[1],
                Url: parts[2],
                IsActive: parts[0] == "1"));
        }
        return tabs;
    }

    private static string? ScriptFor(string lowerAppKey)
    {
        // Chromium-based browsers share the same scripting dictionary.
        if (lowerAppKey is "google chrome" or "chrome" or "arc" or "brave browser" or "brave" or "microsoft edge" or "edge"
            or "chromium" or "vivaldi" or "opera")
        {
            var name = AppleScriptAppName(lowerAppKey);
            return $@"tell application ""{name}""
                set out to """"
                set i to 0
                repeat with w in windows
                    set ai to active tab index of w
                    set i to 0
                    repeat with t in tabs of w
                        set i to i + 1
                        set isActive to (i is equal to ai)
                        set flag to ""0""
                        if isActive then set flag to ""1""
                        set out to out & flag & tab & (title of t) & tab & (URL of t) & linefeed
                    end repeat
                end repeat
                return out
            end tell";
        }
        if (lowerAppKey == "safari")
        {
            return @"tell application ""Safari""
                set out to """"
                repeat with w in windows
                    set ct to current tab of w
                    repeat with t in tabs of w
                        set isActive to (t is ct)
                        set flag to ""0""
                        if isActive then set flag to ""1""
                        set out to out & flag & tab & (name of t) & tab & (URL of t) & linefeed
                    end repeat
                end repeat
                return out
            end tell";
        }
        return null;
    }

    private static string AppleScriptAppName(string key) => key switch
    {
        "chrome" => "Google Chrome",
        "google chrome" => "Google Chrome",
        "arc" => "Arc",
        "brave" => "Brave Browser",
        "brave browser" => "Brave Browser",
        "edge" => "Microsoft Edge",
        "microsoft edge" => "Microsoft Edge",
        "chromium" => "Chromium",
        "vivaldi" => "Vivaldi",
        "opera" => "Opera",
        _ => key,
    };
}
