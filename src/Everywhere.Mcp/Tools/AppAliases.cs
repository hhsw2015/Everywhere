namespace Everywhere.Mcp.Tools;

/// <summary>
/// Maps generic category nouns the user might say ("the browser", "the terminal") to a
/// list of concrete process-name fragments worth probing. Empty when no alias exists,
/// in which case AppResolver falls through to literal substring match.
/// </summary>
internal static class AppAliases
{
    private static readonly Dictionary<string, string[]> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["browser"] = ["arc", "safari", "chrome", "firefox", "edge", "brave", "orion", "vivaldi", "opera"],
        ["浏览器"] = ["arc", "safari", "chrome", "firefox", "edge", "brave", "orion", "vivaldi", "opera"],
        ["chrome"] = ["chrome", "google chrome"],
        ["safari"] = ["safari"],
        ["arc"] = ["arc"],
        ["firefox"] = ["firefox"],

        ["terminal"] = ["terminal", "iterm", "iterm2", "ghostty", "warp", "alacritty", "kitty", "wezterm", "hyper"],
        ["终端"] = ["terminal", "iterm", "iterm2", "ghostty", "warp", "alacritty", "kitty", "wezterm", "hyper"],
        ["iterm"] = ["iterm", "iterm2"],
        ["ghostty"] = ["ghostty"],

        ["editor"] = ["code", "cursor", "zed", "sublime", "intellij", "rider", "pycharm", "webstorm", "goland", "clion", "rubymine", "fleet", "nova", "bbedit", "textmate"],
        ["编辑器"] = ["code", "cursor", "zed", "sublime", "intellij", "rider", "pycharm", "webstorm", "goland", "clion"],
        ["vscode"] = ["code", "visual studio code"],
        ["vs code"] = ["code", "visual studio code"],
        ["cursor"] = ["cursor"],
        ["zed"] = ["zed"],
        ["intellij"] = ["intellij"],

        ["chat"] = ["slack", "discord", "telegram", "qq", "wechat", "weixin", "lark", "feishu", "dingtalk", "teams"],
        ["chat app"] = ["slack", "discord", "telegram", "qq", "wechat"],
        ["聊天"] = ["slack", "discord", "telegram", "qq", "wechat", "weixin", "feishu"],

        ["mail"] = ["mail", "outlook", "spark", "airmail"],
        ["邮件"] = ["mail", "outlook", "spark"],

        ["notes"] = ["notes", "notion", "obsidian", "bear", "evernote", "logseq"],
        ["笔记"] = ["notes", "notion", "obsidian", "bear"],

        ["finder"] = ["finder"],
        ["file explorer"] = ["finder", "explorer"],
        ["文件管理器"] = ["finder", "explorer"],

        ["music"] = ["music", "spotify", "apple music", "netease"],
        ["音乐"] = ["music", "spotify", "netease"],
    };

    public static IReadOnlyList<string> Expand(string hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return [];
        return Map.TryGetValue(hint.Trim(), out var arr) ? arr : [];
    }
}
