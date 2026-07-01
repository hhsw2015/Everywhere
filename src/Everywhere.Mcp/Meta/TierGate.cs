using System.Collections.Frozen;

namespace Everywhere.Mcp.Meta;

/// <summary>
/// SPEC §Phase 6 tier gate. Tools grouped by domain; the session's active
/// domains decide what appears in <c>tools/list</c>. Default tier when
/// <c>EVERYWHERE_MCP_SELFEXPAND=1</c> is <c>search</c>.
/// </summary>
public static class TierGate
{
    /// <summary>Domain groups (matches <see cref="SessionActivations"/> names).</summary>
    public static readonly IReadOnlyDictionary<string, FrozenSet<string>> Domains = new Dictionary<string, FrozenSet<string>>
    {
        ["observation"] = new[]
        {
            "capture_start", "capture_stop", "capture_current", "capture_export",
            "browser_captcha_present", "page_extract_by_rule", "page_save_extraction_rule",
        }.ToFrozenSet(StringComparer.Ordinal),
        ["web_analysis"] = new[]
        {
            "web_verdict_score", "web_signature_scheme", "web_techstack",
            "web_js_search", "web_crypto_scan",
            "web_sourcemap_list_candidates", "web_sourcemap_resolve",
            "web_js_fetch_same_origin",
        }.ToFrozenSet(StringComparer.Ordinal),
        ["memory"] = new[]
        {
            "memory_read", "memory_read_endpoint", "memory_write_endpoint",
            "memory_write_field_map", "memory_write_verify_fixture",
            "memory_append_note", "memory_freshness", "memory_snapshot",
        }.ToFrozenSet(StringComparer.Ordinal),
        ["gates"] = new[]
        {
            "strategy_note_write", "strategy_note_get", "adapter_lint",
        }.ToFrozenSet(StringComparer.Ordinal),
        ["generator"] = new[]
        {
            "adapter_scaffold", "adapter_save", "adapter_verify",
            "adapter_list_local", "adapter_drift_check", "adapter_delete_local",
            "adapter_regenerate", "opendia_smoke_check",
        }.ToFrozenSet(StringComparer.Ordinal),
    };

    /// <summary>Search-tier: the small default surface + 3 meta tools plus a subset of the platform.</summary>
    public static readonly FrozenSet<string> SearchTierTools = new[]
    {
        // native meta
        "list_more_tools", "call_tool", "search_tools",
        // core browser perception
        "browser_snapshot", "browser_get_text", "browser_page_navigate",
        // opencli
        "opencli_list", "opencli_run",
        // observation minimum
        "capture_start", "capture_stop",
        // memory freshness for the runbook step 2
        "memory_freshness",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>All tools that <c>SELFEXPAND</c> gates. Cheaper than reflecting.</summary>
    public static readonly FrozenSet<string> AllSelfExpandTools =
        Domains.Values.SelectMany(s => s).ToFrozenSet(StringComparer.Ordinal);

    public static bool BelongsToDomain(string toolName, string domain)
        => Domains.TryGetValue(domain, out var set) && set.Contains(toolName);
}
