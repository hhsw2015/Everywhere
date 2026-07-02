using System.ComponentModel;
using System.Text.Json.Nodes;
using Everywhere.Mcp.Meta;
using Everywhere.Mcp.OpenCli.Observation;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

/// <summary>
/// SPEC §Phase 6 — <c>search_tools</c> / <c>activate_domain</c> / <c>list_domains</c>.
/// Session activations here are process-wide until wired to per-HTTP-session
/// state in <c>EverywhereMcpHttpHost</c>; tests and single-CLI users see a
/// single virtual session <c>"default"</c>.
/// </summary>
[McpServerToolType]
public sealed class SearchTools
{
    private readonly SessionActivations _sessions;
    private readonly Bm25Index _index;
    private readonly Lazy<AdapterCatalogIndex> _adapterCatalog;
    private static readonly string DefaultSession = "default";

    public SearchTools(SessionActivations sessions, Everywhere.Mcp.OpenCli.OpenCliRuntime? runtime = null)
    {
        _sessions = sessions;
        _index = BuildIndex();
        _adapterCatalog = new Lazy<AdapterCatalogIndex>(() =>
        {
            var idx = new AdapterCatalogIndex();
            idx.Load(runtime?.ManifestPath ?? FindManifestPath());
            return idx;
        });
    }

    private static string FindManifestPath()
    {
        // Fallback probe for dev-tree layout and bundled Resources layout.
        var probes = new[]
        {
            Path.Combine("3rd", "opencli", "cli-manifest.json"),
            Path.Combine("Resources", "opencli", "cli-manifest.json"),
            Path.Combine("opencli", "cli-manifest.json"),
        };
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var rel in probes)
            {
                var probe = Path.Combine(dir.FullName, rel);
                if (File.Exists(probe)) return probe;
            }
            dir = dir.Parent;
        }
        return "";
    }

    private static Bm25Index BuildIndex()
    {
        var idx = new Bm25Index();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string name)
        {
            if (seen.Add(name)) idx.Add(new Bm25Index.Doc(name, DescribeTool(name)));
        }
        foreach (var (_, tools) in TierGate.Domains)
            foreach (var t in tools) Add(t);
        foreach (var t in TierGate.SearchTierTools) Add(t);
        return idx;
    }

    [McpServerTool(Name = "search_tools")]
    [Description("BM25 keyword search over the self-expanding tool catalog. Returns top-K {name, description_snippet, score, tier}.")]
    public string SearchToolsCmd(string query, int? top_k = null)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        var hits = _index.Search(query, top_k ?? 5);
        var arr = new JsonArray();
        foreach (var h in hits)
        {
            arr.Add(new JsonObject
            {
                ["name"] = h.Name,
                ["description_snippet"] = h.Description,
                ["score"] = h.Score,
                ["tier"] = TierOf(h.Name),
            });
        }
        return arr.ToJsonString();
    }

    [McpServerTool(Name = "activate_domain")]
    [Description(
        "Activate a domain group so its tools appear in tools/list for the current session. " +
        "Names: observation | web_analysis | memory | gates | generator | full.")]
    public string ActivateDomain(string name)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        if (!_sessions.Activate(DefaultSession, name))
            return new JsonObject { ["ok"] = false, ["code"] = "UNKNOWN_DOMAIN", ["message"] = name }.ToJsonString();
        var set = _sessions.Get(DefaultSession);
        return new JsonObject
        {
            ["active_domains"] = new JsonArray(set.Select(s => (JsonNode)s).ToArray()),
        }.ToJsonString();
    }

    [McpServerTool(Name = "search_adapters")]
    [Description(
        "BM25 keyword search across the merged adapter catalog: vendored (3rd/opencli/cli-manifest.json) " +
        "+ locally-generated (~/.everywhere/adapters/). Returns top-K {site, name, description, origin, strategy, score}.")]
    public string SearchAdapters(string query, int? top_k = null)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        var idx = _adapterCatalog.Value;
        var hits = idx.Search(query ?? "", top_k ?? 5);
        var arr = new JsonArray();
        foreach (var h in hits)
        {
            arr.Add(new JsonObject
            {
                ["site"] = h.Entry.Site,
                ["name"] = h.Entry.Name,
                ["description"] = h.Entry.Description,
                ["origin"] = h.Entry.Origin,
                ["strategy"] = h.Entry.Strategy,
                ["score"] = h.Score,
            });
        }
        return arr.ToJsonString();
    }

    [McpServerTool(Name = "list_domains")]
    [Description("Enumerate available domain groups and whether they're active in this session.")]
    public string ListDomains()
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        var active = _sessions.Get(DefaultSession);
        var arr = new JsonArray();
        foreach (var (domain, tools) in TierGate.Domains)
        {
            arr.Add(new JsonObject
            {
                ["name"] = domain,
                ["tool_count"] = tools.Count,
                ["active"] = active.Contains(domain) || active.Contains("full"),
            });
        }
        return arr.ToJsonString();
    }

    private static string TierOf(string tool)
    {
        if (TierGate.SearchTierTools.Contains(tool)) return "search";
        foreach (var (domain, tools) in TierGate.Domains)
            if (tools.Contains(tool)) return domain;
        return "long_tail";
    }

    private static string Err(string code, string message)
        => new JsonObject { ["ok"] = false, ["code"] = code, ["message"] = message }.ToJsonString();

    private static string DescribeTool(string name) => name switch
    {
        "capture_start" => "Start a capture session bound to a browser tab; returns session_id.",
        "capture_stop" => "Finalize a capture session and return sanitized JSON.",
        "capture_current" => "Live snapshot of a running capture.",
        "capture_export" => "Write sanitized capture JSON under ~/.everywhere/captures/.",
        "browser_captcha_present" => "Detect reCAPTCHA v2/v3, Cloudflare Turnstile, hCaptcha in the active tab.",
        "page_extract_by_rule" => "Apply the extraction rulebook to the active tab.",
        "page_save_extraction_rule" => "Persist a URL-pattern → CSS/XPath selector rule.",
        "web_verdict_score" => "Score every captured request likely_data / maybe_data / noise / blocked.",
        "web_signature_scheme" => "Detect API signature scheme: jwt | bearer | basic | hmac_sha256.",
        "web_techstack" => "Detect frontend framework / build tool / state library.",
        "web_js_search" => "Regex search over indexed JS bodies (redactor-safe snippets).",
        "web_crypto_scan" => "Scan captured JS body for crypto API usage.",
        "web_sourcemap_list_candidates" => "List sourcemap references discovered in the capture.",
        "web_sourcemap_resolve" => "Resolve compiled (url,line,col) → original source through the capture's map.",
        "web_js_fetch_same_origin" => "SSRF-safe same-origin JS fetch for offline analysis.",
        "memory_read" => "Read a site's memory summary (fresh / stale / cold / cold-cold).",
        "memory_read_endpoint" => "Read a single stored EndpointSpec.",
        "memory_write_endpoint" => "Persist an EndpointSpec; MERGE_CONFLICT without force.",
        "memory_write_field_map" => "Persist a raw-key → FieldMapEntry mapping.",
        "memory_write_verify_fixture" => "Persist a 4-tuple VerifyFixture for the site/cmd.",
        "memory_append_note" => "Append a timestamped freeform note.",
        "memory_freshness" => "Classify site memory freshness.",
        "memory_snapshot" => "Write a sanitized capture snapshot fixture (rotates last 5).",
        "strategy_note_write" => "Persist a StrategyNote after validating evidence / replay / mutation.",
        "strategy_note_get" => "Read a StrategyNote from memory.",
        "adapter_lint" => "Run G3-G8 lints over adapter source.",
        "adapter_scaffold" => "Render the OpenCLI adapter skeleton + LLM prompt for a capture.",
        "adapter_save" => "Persist a generated adapter after passing G3-G8.",
        "adapter_verify" => "Run G3-G9 lints against a stored local adapter.",
        "adapter_list_local" => "List locally-generated adapters.",
        "adapter_drift_check" => "Compare current adapter output to stored last_success_hash.",
        "adapter_delete_local" => "Delete a locally-generated adapter.",
        "adapter_regenerate" => "Regenerate a local adapter using a fresh capture.",
        "opendia_smoke_check" => "Verify the OpenDia extension exposes every required browser_* tool.",
        "list_more_tools" => "Long-tail tool catalog gated by CoreToolGate.",
        "call_tool" => "Invoke any registered tool by name.",
        "search_tools" => "BM25 search over the self-expanding tool catalog.",
        "browser_snapshot" => "DOM/ARIA tree of the active tab.",
        "browser_get_text" => "Extract innerText from the active tab (optionally scoped).",
        "browser_page_navigate" => "Navigate the active tab to a URL.",
        "opencli_list" => "List installed OpenCLI adapters.",
        "opencli_run" => "Execute an OpenCLI adapter and return typed rows.",
        _ => name,
    };
}
