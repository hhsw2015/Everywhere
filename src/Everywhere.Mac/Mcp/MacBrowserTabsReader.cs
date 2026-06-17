using System.Text;
using Everywhere.Mcp.Snapshot;

namespace Everywhere.Mac.Mcp;

/// <summary>
/// macOS browser-tabs reader. Per-app AppleScript: Safari + Chromium derivatives
/// (Chrome/Arc/Brave/Edge/Chromium/Vivaldi/Opera) — they all share the same
/// scripting dictionary so one template suffices.
/// </summary>
public sealed class MacBrowserTabsReader(IAppleScriptRunner runner) : IBrowserTabsReader
{
    // Closed allow-list: every entry is a known-safe AppleScript application name.
    // No fallthrough to caller-supplied strings, so the AppleScript template is
    // never interpolated with attacker-controlled data.
    private static readonly Dictionary<string, string> ChromiumApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "Google Chrome",
        ["google chrome"] = "Google Chrome",
        ["arc"] = "Arc",
        ["brave"] = "Brave Browser",
        ["brave browser"] = "Brave Browser",
        ["edge"] = "Microsoft Edge",
        ["microsoft edge"] = "Microsoft Edge",
        ["chromium"] = "Chromium",
        ["vivaldi"] = "Vivaldi",
        ["opera"] = "Opera",
    };

    public BrowserTabsResult GetTabs(string appKey)
    {
        if (string.IsNullOrWhiteSpace(appKey))
            return new BrowserTabsResult(BrowserTabsStatus.NotSupported, []);

        var script = ScriptFor(appKey);
        if (script is null)
            return new BrowserTabsResult(BrowserTabsStatus.NotSupported, []);

        var result = runner.Run(script);
        switch (result.Status)
        {
            case AppleScriptStatus.PermissionDenied:
                return new BrowserTabsResult(BrowserTabsStatus.PermissionDenied, [], result.ErrorMessage);
            case AppleScriptStatus.NotSupported:
                return new BrowserTabsResult(BrowserTabsStatus.NotSupported, [], result.ErrorMessage);
            case AppleScriptStatus.Failed:
                return new BrowserTabsResult(BrowserTabsStatus.PermissionDenied, [], result.ErrorMessage);
        }

        var tabs = ParseTabs(result.Output);
        return new BrowserTabsResult(BrowserTabsStatus.Ok, tabs);
    }

    private static List<BrowserTab> ParseTabs(string? raw)
    {
        var tabs = new List<BrowserTab>();
        if (string.IsNullOrEmpty(raw)) return tabs;

        // Each line: `flag\x1Ftitle\x1Furl` — using ASCII Unit Separator avoids
        // the chance that a page title containing a tab character mis-aligns columns.
        // (Title still cannot contain \x1F or a record-separating \x1E.)
        foreach (var line in raw.Split('\x1E'))
        {
            var trimmed = line.Trim('\r', '\n', ' ');
            if (string.IsNullOrEmpty(trimmed)) continue;
            var parts = trimmed.Split('\x1F', 3);
            if (parts.Length < 3) continue;
            tabs.Add(new BrowserTab(
                Title: parts[1],
                Url: parts[2],
                IsActive: parts[0] == "1"));
        }
        return tabs;
    }

    private static string? ScriptFor(string appKey)
    {
        var lower = appKey.ToLowerInvariant();
        if (lower == "safari")
        {
            return BuildSafariScript();
        }
        if (ChromiumApps.TryGetValue(lower, out var canonicalName))
        {
            return BuildChromiumScript(canonicalName);
        }
        return null;
    }

    // \x1F = ASCII Unit Separator, \x1E = ASCII Record Separator.
    // Splits inside titles are vanishingly rare for these control bytes vs. \t / \n.
    // Chromium browsers (Chrome / Brave / Edge / Vivaldi / Opera / Chromium) expose
    // `active tab index of window`. Arc uses the same dictionary BUT lacks that
    // property — so we identify the active tab by reference equality with
    // `active tab of w` instead, which every Chromium-derived browser supports.
    private static string BuildChromiumScript(string canonicalAppName) =>
        $@"tell application ""{canonicalAppName}""
            set out to """"
            set US to (ASCII character 31)
            set RS to (ASCII character 30)
            repeat with w in windows
                try
                    set at to active tab of w
                on error
                    set at to missing value
                end try
                repeat with t in tabs of w
                    set isActive to false
                    try
                        if at is not missing value and (t is at) then set isActive to true
                    end try
                    set flag to ""0""
                    if isActive then set flag to ""1""
                    set out to out & flag & US & (title of t) & US & (URL of t) & RS
                end repeat
            end repeat
            return out
        end tell";

    private static string BuildSafariScript() =>
        @"tell application ""Safari""
            set out to """"
            set US to (ASCII character 31)
            set RS to (ASCII character 30)
            repeat with w in windows
                set ct to current tab of w
                repeat with t in tabs of w
                    set isActive to (t is ct)
                    set flag to ""0""
                    if isActive then set flag to ""1""
                    set out to out & flag & US & (name of t) & US & (URL of t) & RS
                end repeat
            end repeat
            return out
        end tell";
}
