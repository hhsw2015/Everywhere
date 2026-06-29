using System.ComponentModel;
using System.Text;
using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenDia;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

/// <summary>
/// Meta tools that let the agent reach the long-tail surface that
/// <see cref="CoreToolGate"/> hides from the default <c>tools/list</c>.
///
/// Surface contract:
///  - <c>list_more_tools(category?)</c> → human-readable catalog of long-tail tools.
///  - <c>call_tool(name, arguments?)</c> → dispatch to any registered tool by name.
///
/// Without these the gate is a one-way door: the long-tail tools exist but
/// are invisible to the agent. With these the gate is a soft filter — common
/// tools have direct schemas in <c>tools/list</c>, niche tools come into reach
/// only when the task needs them.
/// </summary>
[McpServerToolType]
public sealed class MetaTools
{
    private readonly OpenDiaBridge? _bridge;
    private readonly IServiceProvider _services;

    public MetaTools(IServiceProvider services, OpenDiaBridge? bridge = null)
    {
        _services = services;
        _bridge = bridge;
    }

    [McpServerTool(Name = "list_more_tools")]
    [Description(
        "🔎 List long-tail tools by category. Use when the default tools/list " +
        "doesn't cover what you need. Browser long-tail: cookies, CDP eval, " +
        "network capture, React debug, find_by_role/label/testid, device emulation, " +
        "auth/state. Native long-tail: doc_read_xlsx/pptx/epub/html/txt, drag, " +
        "perform_secondary_action, list_apps, expand_element, get_idle_time. " +
        "Returns a markdown catalog (name + one-line description) per category. " +
        "Pair with `call_tool(name, args)` to invoke. " +
        "Categories: action_browser | action_macos | perception_active | " +
        "perception_content | debug | config | (omit for overview).")]
    public string ListMoreTools(
        [Description("Optional category filter. Omit for category overview with counts.")]
        string? category = null)
    {
        var (browserTools, _) = SnapshotBrowserCatalog();

        if (string.IsNullOrEmpty(category))
        {
            return BuildOverview(browserTools);
        }

        return BuildCategoryListing(category, browserTools);
    }

    [McpServerTool(Name = "call_tool")]
    [Description(
        "🛠️ Invoke any registered tool by name (including long-tail tools not " +
        "shown in tools/list). Use after `list_more_tools` to drive a niche " +
        "capability without polluting the default tools list. Errors come " +
        "back as the target's own error envelope — adjust args and retry.")]
    public async Task<string> CallTool(
        [Description("Target tool name, e.g. 'browser_cookies_get' or 'doc_read_xlsx'.")]
        string name,
        [Description(
            "Arguments as a JSON object string, e.g. '{\"url\":\"https://...\",\"timeout\":5000}'. " +
            "Pass '{}' or omit for tools that take no args. " +
            "MUST be a JSON string, not an inline object — schemas built by " +
            "the MCP SDK can't represent free-form objects safely here.")]
        string? arguments_json = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(name))
            return ErrorEnvelope("call_tool requires `name`.");

        JsonObject? args = null;
        if (!string.IsNullOrWhiteSpace(arguments_json))
        {
            try
            {
                args = JsonNode.Parse(arguments_json) as JsonObject
                    ?? throw new InvalidOperationException("arguments_json must parse to a JSON object");
            }
            catch (Exception ex)
            {
                return ErrorEnvelope($"call_tool: arguments_json is not a JSON object — {ex.Message}");
            }
        }

        // Browser path: forward via OpenDia bridge.
        if (name.StartsWith(OpenDiaToolListBuilder.Prefix, StringComparison.Ordinal))
        {
            if (_bridge is null)
                return ErrorEnvelope("opendia-not-connected");
            var origName = name.Substring(OpenDiaToolListBuilder.Prefix.Length);
            try
            {
                var raw = await _bridge.CallToolAsync(origName, args, ct: ct).ConfigureAwait(false);
                return raw is null ? "{}" : raw.ToJsonString();
            }
            catch (Exception ex)
            {
                return ErrorEnvelope(ex.Message);
            }
        }

        // Native path: dispatch by reflection over the [McpServerTool]
        // surface in this assembly. Long-tail native tools (e.g.
        // doc_read_xlsx, drag, perform_secondary_action, list_apps) live
        // here — they're hidden from tools/list by CoreToolGate but stay
        // reachable through this path.
        return await NativeToolDispatcher.InvokeAsync(_services, name, args, ct).ConfigureAwait(false);
    }

    private static string ErrorEnvelope(string message) =>
        new JsonObject
        {
            ["ok"] = false,
            ["error"] = message,
        }.ToJsonString();

    // ---------------- catalog -----------------------------------------------

    private (List<JsonObject> browserTools, int browserCount) SnapshotBrowserCatalog()
    {
        if (_bridge is null) return (new List<JsonObject>(), 0);
        var copy = _bridge.AvailableTools.ToList();
        return (copy, copy.Count);
    }

    private static string BuildOverview(List<JsonObject> browserTools)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Long-tail tools");
        sb.AppendLine();
        sb.AppendLine("Default tools/list shows core perception + a few high-frequency");
        sb.AppendLine("operations. Long-tail tools are hidden to save context tokens.");
        sb.AppendLine("Call `list_more_tools(category)` for a listing, then");
        sb.AppendLine("`call_tool(name=..., arguments={...})` to invoke.");
        sb.AppendLine();
        sb.AppendLine("| category | count | examples |");
        sb.AppendLine("|---|---|---|");

        // Browser long-tail buckets.
        foreach (var cat in new[] { "action_browser", "debug", "config" })
        {
            Func<string, bool> matcher = cat switch
            {
                "action_browser" => n => !IsDebug(n) && !IsConfig(n),
                "debug" => IsDebug,
                "config" => IsConfig,
                _ => _ => false,
            };
            int count = 0;
            var examples = new List<string>();
            foreach (var t in browserTools)
            {
                var name = ToolName(t);
                if (string.IsNullOrEmpty(name)) continue;
                var prefixed = PrefixIfNeeded(name!);
                if (CoreToolGate.IsCore(prefixed)) continue;
                if (!matcher(prefixed)) continue;
                count++;
                if (examples.Count < 3) examples.Add(prefixed);
            }
            sb.AppendLine($"| {cat} | {count} | {string.Join(", ", examples)} |");
        }

        // Native long-tail buckets, keyed by name pattern.
        AppendNativeRow(sb, "perception_active",
            n => n is "list_apps" or "expand_element" or "get_browser_tabs" or "get_idle_time" or "pick_element");
        AppendNativeRow(sb, "perception_content",
            n => n is "doc_read_xlsx" or "doc_read_pptx" or "doc_read_epub" or "doc_read_html" or "doc_read_txt" or "read_whiteboard_image");
        AppendNativeRow(sb, "action_macos",
            n => n is "drag" or "perform_secondary_action" or "clipboard_write" or "clipboard_copy" or "clipboard_read" or "clipboard_paste");

        return sb.ToString();
    }

    private static void AppendNativeRow(StringBuilder sb, string label, Func<string, bool> match)
    {
        var hits = CoreToolGate.NativeLongTail.Where(match).ToList();
        var examples = hits.Take(3);
        sb.AppendLine($"| {label} | {hits.Count} | {string.Join(", ", examples)} |");
    }

    private static string BuildCategoryListing(string category, List<JsonObject> browserTools)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {category} (long-tail)");
        sb.AppendLine();

        int n = 0;

        // Browser long-tail rows.
        Func<string, bool>? browserMatcher = category switch
        {
            "action_browser" => x => !IsDebug(x) && !IsConfig(x),
            "debug" => IsDebug,
            "config" => IsConfig,
            _ => null,
        };
        if (browserMatcher is not null)
        {
            foreach (var spec in browserTools)
            {
                var nRaw = ToolName(spec);
                if (string.IsNullOrEmpty(nRaw)) continue;
                var prefixed = PrefixIfNeeded(nRaw!);
                if (CoreToolGate.IsCore(prefixed)) continue;
                if (!browserMatcher(prefixed)) continue;
                var desc = spec["description"]?.GetValue<string>() ?? "";
                var oneline = FirstLine(desc);
                sb.AppendLine($"- `{prefixed}` — {oneline}");
                n++;
            }
        }

        // Native long-tail rows.
        foreach (var (name, oneliner) in NativeCatalog(category))
        {
            sb.AppendLine($"- `{name}` — {oneliner}");
            n++;
        }

        if (n == 0)
            return $"No long-tail tools in category '{category}'. " +
                   $"Valid: action_browser | action_macos | perception_active | " +
                   $"perception_content | debug | config.";

        sb.AppendLine();
        sb.AppendLine("Invoke any of these with: `call_tool(name=\"...\", arguments={...})`.");
        return sb.ToString();
    }

    /// <summary>
    /// Hand-curated one-liners for native long-tail tools. Keep in sync with
    /// CoreToolGate.NativeLongTail.
    /// </summary>
    private static IEnumerable<(string Name, string OneLiner)> NativeCatalog(string category) =>
        category switch
        {
            "perception_active" => new[]
            {
                ("list_apps", "List every running app with at least one top-level window."),
                ("expand_element", "Re-walk an indexed a11y subtree with a fresh node budget."),
                ("get_idle_time", "Seconds since the user last touched any input device."),
                ("pick_element", "Open the visual element picker for the user to point at a target."),
            },
            "perception_content" => new[]
            {
                ("doc_read_xlsx", "Extract sheets from a .xlsx as CSV-ish text."),
                ("doc_read_pptx", "Extract text from a .pptx slide deck."),
                ("doc_read_epub", "Extract chapters from an .epub in reading order."),
                ("doc_read_html", "Extract visible text from local HTML/HTM."),
                ("doc_read_txt", "Read a UTF-8 / GB18030 / Latin-1 text file."),
                ("read_whiteboard_image", "Fetch one image from a prior read_whiteboard payload."),
            },
            "action_macos" => new[]
            {
                ("drag", "Press at (from), drag to (to), release. Brings target to foreground."),
                ("perform_secondary_action", "Invoke a named AX action (AXShowMenu / AXIncrement) on an indexed element."),
                ("clipboard_write", "Replace pasteboard with a string (DANGEROUS — overwrites user clipboard)."),
                ("clipboard_copy", "Alias of clipboard_write."),
                ("clipboard_read", "Read pasteboard (alias of get_clipboard)."),
                ("clipboard_paste", "Read pasteboard (SPEC alias)."),
            },
            _ => Array.Empty<(string, string)>(),
        };

    private static string? ToolName(JsonObject spec) =>
        spec["name"]?.GetValue<string>();

    private static string PrefixIfNeeded(string n) =>
        n.StartsWith(OpenDiaToolListBuilder.Prefix, StringComparison.Ordinal)
            ? n
            : OpenDiaToolListBuilder.Prefix + n;

    private static string FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var idx = s.IndexOfAny(new[] { '\n', '.' });
        if (idx < 0) idx = Math.Min(120, s.Length);
        return s.Substring(0, Math.Min(idx, s.Length)).Trim();
    }

    private static bool IsDebug(string n) =>
        n.Contains("eval", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("cdp", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("react", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("trace", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("profiler", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("vitals", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("inspect", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("console", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("errors", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("network_har", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("network_requests", StringComparison.OrdinalIgnoreCase);

    private static bool IsConfig(string n) =>
        n.Contains("set_viewport", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("set_geo", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("set_media", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("set_offline", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("set_headers", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("set_credentials", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("device", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("emulate", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("init_script", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("auth_", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("state_", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("storage_", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("cookies_", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("route", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("network_request", StringComparison.OrdinalIgnoreCase);
}
