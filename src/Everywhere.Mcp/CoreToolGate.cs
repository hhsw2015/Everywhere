using System.Collections.Frozen;
using Everywhere.Mcp.OpenDia;

namespace Everywhere.Mcp;

/// <summary>
/// Gate that decides which MCP tools are exposed in <c>tools/list</c> by default.
///
/// The full Everywhere surface is ~200 tools (~85KB JSON ≈ 25K input tokens).
/// The vast majority of that bulk lives in the <c>browser_*</c> family forwarded
/// from the OpenDia extension (~165 tools). The native side is small (~39
/// tools) and we leave it fully exposed — its tools are the agent's perception
/// layer and the per-tool description budget is already tight.
///
/// Positioning: Everywhere is the agent's perception layer for this machine.
/// Operations exist to advance perception, not as ends. The <c>browser_*</c>
/// CORE list reflects that priority — passive perception first, active
/// perception second, exploratory operations third.
///
/// Reachability: long-tail <c>browser_*</c> tools stay reachable on demand
/// via the meta tools <c>list_more_tools</c> + <c>call_tool</c> (see
/// <see cref="Tools.MetaTools"/>).
/// </summary>
internal static class CoreToolGate
{
    /// <summary>
    /// Decide whether to hide a tool from <c>tools/list</c>.
    /// browser_* tools that are not in CoreBrowserTools → hide.
    /// Native tools in NativeLongTail → hide.
    /// Everything else (native core, meta tools) → keep.
    /// </summary>
    public static bool ShouldFilter(string? toolName)
    {
        if (string.IsNullOrEmpty(toolName)) return false;
        if (!FilterEnabled) return false;
        if (toolName!.StartsWith(OpenDiaToolListBuilder.Prefix, StringComparison.Ordinal))
            return !CoreBrowserTools.Contains(toolName);
        // opencli_list + opencli_run stay core; opencli_describe is a
        // between-step tool the agent reaches for on demand — hide it
        // from tools/list to save ~500 tokens; call_tool still reaches it.
        if (toolName!.StartsWith("opencli_", StringComparison.Ordinal))
        {
            if (!OpenCliEnabled) return true;
            return toolName == "opencli_describe";
        }
        return NativeLongTail.Contains(toolName);
    }

    /// <summary>
    /// SPEC §6.7. Default ON — the surface is only 3 tools
    /// (opencli_list / describe / run), so the system-prompt token cost
    /// is bounded (≈600 tokens for the descriptions). The 1257
    /// individual adapter commands are reachable via opencli_list +
    /// opencli_run, mirroring the list_more_tools / call_tool lazy
    /// pattern used for the long-tail browser_* surface. Set
    /// <c>EVERYWHERE_MCP_OPENCLI=0</c> to opt out.
    /// </summary>
    public static bool OpenCliEnabled => _openCliEnabled.Value;

    private static readonly Lazy<bool> _openCliEnabled = new(() =>
        Environment.GetEnvironmentVariable("EVERYWHERE_MCP_OPENCLI") is not "0");

    /// <summary>
    /// Convenience inverse for code that prefers a positive predicate.
    /// </summary>
    public static bool IsCore(string toolName) => !ShouldFilter(toolName);

    /// <summary>
    /// Whether the gate is active. Default true. Set
    /// <c>EVERYWHERE_MCP_FULL=1</c> to expose every tool — needed for
    /// bench parity tests that compare against agent-browser's full surface.
    /// Cached on first read so process-wide gate behaviour is consistent;
    /// changing the env var after process start has no effect.
    /// </summary>
    public static bool FilterEnabled => _filterEnabled.Value;

    private static readonly Lazy<bool> _filterEnabled = new(() =>
        Environment.GetEnvironmentVariable("EVERYWHERE_MCP_FULL") is not "1");

    /// <summary>
    /// Native tools that are always exposed (in addition to the implicit
    /// non-long-tail surface). Listed here only when they need to NOT be
    /// hidden by anything else — purely documentary today. Real gating
    /// happens through <see cref="NativeLongTail"/> below.
    /// </summary>
    public static readonly FrozenSet<string> CoreNativeTools = new[]
    {
        // web_search + web_fetch_url: content perception via configured
        // search provider. Saves ~10× tokens vs WebFetch on viewed URLs
        // by routing through the user's existing API quota + pool.
        "web_search",
        "web_fetch_url",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Native tools hidden from default tools/list. Reached via call_tool.
    /// These are duplicates / aliases / niche tools that don't earn their
    /// description-token budget for a typical agent task.
    /// </summary>
    public static readonly FrozenSet<string> NativeLongTail = new[]
    {
        // Clipboard duplicates — get_clipboard is the canonical reader.
        "clipboard_read",
        "clipboard_paste",
        "clipboard_write",
        "clipboard_copy",

        // Doc readers — pdf+docx/xlsx/pptx/epub/html/txt all live here now.
        // The high-frequency shape (agent extracts text from a user-named
        // file path) is well-served by call_tool + list_more_tools, and the
        // pdf/docx descriptions cost ~800 tokens together in tools/list.
        "doc_read_pdf",
        "doc_read_docx",
        "doc_read_xlsx",
        "doc_read_pptx",
        "doc_read_epub",
        "doc_read_html",
        "doc_read_txt",

        // Active perception duplicates / niche.
        "list_apps",                 // get_app_context covers the common case
        // NOTE: get_browser_tabs intentionally NOT here — it's the
        // no-extension fallback for browser tabs; users without OpenDia
        // installed would otherwise lose all browser-tab perception.
        "expand_element",            // re-walk with bigger budget — niche
        "get_idle_time",             // diagnostic only
        "read_whiteboard_image",     // pair tool to read_whiteboard

        // macOS operation niche.
        "drag",
        "perform_secondary_action",
        "pick_element",              // user-driven picker; read_pick consumes the result
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Browser tools the agent always sees in <c>tools/list</c>. Hand-curated
    /// from agent-browser parity-matrix tier=core + perception priorities.
    /// Long-tail browser tools (~150) are reached via <c>list_more_tools</c>.
    /// </summary>
    public static readonly FrozenSet<string> CoreBrowserTools = new[]
    {
        // Active perception in the browser
        "browser_snapshot",          // DOM/ARIA tree with @refN anchors
        "browser_get_url",           // current URL via extension
        "browser_get_text",          // node innerText (content perception)
        "browser_screenshot",        // viewport visual fallback

        // Exploratory operations that advance perception
        "browser_click",
        "browser_fill",
        "browser_press",
        "browser_scroll",
        "browser_page_navigate",
        "browser_page_wait_for",     // covers selector / text / url / load_state / predicate
    }.ToFrozenSet(StringComparer.Ordinal);
}
