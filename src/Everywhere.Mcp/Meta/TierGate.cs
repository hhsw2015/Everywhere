using System.Collections.Frozen;

namespace Everywhere.Mcp.Meta;

/// <summary>
/// SPEC §Phase 6 tier gate. Tools grouped by domain; the session's active
/// domains decide what appears in <c>tools/list</c>. Default tier when
/// self-expand is enabled is <c>search</c>.
/// </summary>
public static class TierGate
{
    /// <summary>Domain groups (matches <see cref="SessionActivations"/> names, and SPEC §Phase 6).</summary>
    public static readonly IReadOnlyDictionary<string, FrozenSet<string>> Domains = new Dictionary<string, FrozenSet<string>>
    {
        // SPEC §Phase 6 names the observation domain "browser_core". We
        // keep an "observation" alias below in DomainAliases for backwards
        // compat with earlier releases.
        ["browser_core"] = new[]
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
        // SPEC docs/specs/opendia-cebian-merge.md §Phase 4 — chat bus tools
        // that proxy sidepanel chat state to daemon-side MCP agents (Claude
        // Code, Cursor, …). Gated so untrusted callers can't lurk in the
        // sidepanel's private conversation.
        ["chat"] = new[]
        {
            "chat_list", "chat_read", "chat_send",
            "chat_create", "chat_delete", "chat_subscribe",
        }.ToFrozenSet(StringComparer.Ordinal),
    };

    /// <summary>Aliases accepted by <see cref="SessionActivations.Activate"/> and normalised to canonical names.</summary>
    public static readonly IReadOnlyDictionary<string, string> DomainAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["observation"] = "browser_core",
    };

    /// <summary>Search-tier: 3 meta tools + a subset of the platform, always visible.</summary>
    public static readonly FrozenSet<string> SearchTierTools = new[]
    {
        "list_more_tools", "call_tool", "search_tools", "search_adapters",
        "browser_snapshot", "browser_get_text", "browser_page_navigate",
        "opencli_list", "opencli_run",
        "capture_start", "capture_stop",
        "memory_freshness",
        "list_domains", "activate_domain",
        "opendia_smoke_check",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>All tools that <c>SELFEXPAND</c> gates. Cheaper than reflecting.</summary>
    public static readonly FrozenSet<string> AllSelfExpandTools =
        Domains.Values.SelectMany(s => s).ToFrozenSet(StringComparer.Ordinal);

    public static bool BelongsToDomain(string toolName, string domain)
        => Domains.TryGetValue(domain, out var set) && set.Contains(toolName);
}
