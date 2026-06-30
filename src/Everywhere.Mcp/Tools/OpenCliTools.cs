using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenCli;
using Everywhere.Mcp.OpenDia;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

/// <summary>
/// MCP <c>opencli_list</c> / <c>opencli_describe</c> / <c>opencli_run</c>.
/// SPEC docs/specs/everywhere-opencli-adapters.md §4.
///
/// The three tools share <see cref="OpenCliRuntime"/>; the runtime
/// lazy-boots V8 on first use, so installs that never call these tools
/// pay no startup cost (SPEC §8 Phase 3 'Restart-tolerance').
/// </summary>
[McpServerToolType]
public sealed class OpenCliTools(OpenCliRuntime runtime, OpenDiaBridge? bridge = null)
{
    private static string Envelope(bool ok, string? site, string? name, string? error, string? code, JsonNode? data = null)
    {
        var o = new JsonObject
        {
            ["schema_version"] = "1",
            ["ok"] = ok,
        };
        if (site is not null) o["site"] = site;
        if (name is not null) o["name"] = name;
        if (error is not null) o["error"] = error;
        if (code is not null) o["code"] = code;
        if (data is not null) o["data"] = data;
        return o.ToJsonString();
    }

    [McpServerTool(Name = "opencli_list")]
    [Description(
        "List OpenCLI site adapters Everywhere can run. With no args " +
        "returns the SITE INDEX only — site name + command count + " +
        "small description (cheap, ~3 KB). Pass `site` to drill into " +
        "one site and get every command on it; pass `query` to fuzzy-" +
        "match across the full registry. Pair with opencli_describe for " +
        "a command's full schema; pair with opencli_run to execute one.")]
    public async Task<string> OpenCliList(
        [Description("Optional site filter (e.g. \"bilibili\"). When set, returns every command for just that site instead of the site index.")] string? site = null,
        [Description("Optional case-insensitive substring match against site/name/description. Caps at 60 hits.")] string? query = null,
        CancellationToken ct = default)
    {
        try
        {
            var defs = await runtime.ListAsync(ct).ConfigureAwait(false);

            // Site drill-down.
            if (!string.IsNullOrWhiteSpace(site))
            {
                var siteKey = site.Trim();
                var matches = defs.Where(d => string.Equals(d.Site, siteKey, StringComparison.OrdinalIgnoreCase)).ToList();
                var arr = new JsonArray();
                foreach (var d in matches) arr.Add(d.ToListEntry());
                return new JsonObject
                {
                    ["schema_version"] = "1",
                    ["mode"] = "site",
                    ["site"] = siteKey,
                    ["commands"] = arr,
                    ["upstream_sha"] = runtime.UpstreamSha,
                }.ToJsonString();
            }

            // Fuzzy match.
            if (!string.IsNullOrWhiteSpace(query))
            {
                const int Cap = 60;
                var q = query.Trim();
                var allMatches = defs
                    .Where(d => d.Site.Contains(q, StringComparison.OrdinalIgnoreCase)
                             || d.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                             || (d.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
                    .ToList();
                var shown = allMatches.Take(Cap).ToList();
                var arr = new JsonArray();
                foreach (var d in shown) arr.Add(d.ToListEntry());
                return new JsonObject
                {
                    ["schema_version"] = "1",
                    ["mode"] = "query",
                    ["query"] = q,
                    ["commands"] = arr,
                    ["total_matches"] = allMatches.Count,
                    ["truncated"] = allMatches.Count > Cap,
                    ["upstream_sha"] = runtime.UpstreamSha,
                }.ToJsonString();
            }

            // Default: site index — collapses 1257 commands to ~170 sites.
            var indexArr = new JsonArray();
            foreach (var g in defs.GroupBy(d => d.Site).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var sample = g.First();
                indexArr.Add(new JsonObject
                {
                    ["site"] = g.Key,
                    ["count"] = g.Count(),
                    ["description"] = sample.Domain ?? sample.Description,
                });
            }
            return new JsonObject
            {
                ["schema_version"] = "1",
                ["mode"] = "index",
                ["sites"] = indexArr,
                ["total_commands"] = defs.Count,
                ["hint"] = "call opencli_list({site:\"<name>\"}) to drill into a site, or opencli_list({query:\"<text>\"}) for fuzzy search",
                ["upstream_sha"] = runtime.UpstreamSha,
            }.ToJsonString();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Envelope(false, null, null, ex.Message, "RUNTIME_HOST_ERROR");
        }
    }

    [McpServerTool(Name = "opencli_describe")]
    [Description(
        "Describe one OpenCLI command: full args/columns schema, " +
        "strategy, whether it needs a browser. Use after opencli_list " +
        "narrows you to a site/name.")]
    public async Task<string> OpenCliDescribe(
        [Description("Site identifier (e.g. \"36kr\").")] string site,
        [Description("Command name within the site (e.g. \"news\").")] string name,
        CancellationToken ct = default)
    {
        try
        {
            var def = await runtime.Resolve(site, name, ct).ConfigureAwait(false);
            if (def is null)
                return Envelope(false, site, name, $"adapter {site}/{name} not registered", "RUNTIME_NOT_FOUND");
            return def.ToDescribeJson().ToJsonString();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Envelope(false, site, name, ex.Message, "RUNTIME_HOST_ERROR");
        }
    }

    [McpServerTool(Name = "opencli_run")]
    [Description(
        "Run an OpenCLI command. PUBLIC adapters work even without a " +
        "browser extension; cookie/intercept/ui adapters require " +
        "OpenDia to be connected — without it, run returns " +
        "{ok:false, code:\"BROWSER_NOT_READY\"}.")]
    public async Task<string> OpenCliRun(
        [Description("Site identifier (from opencli_list).")] string site,
        [Description("Command name (from opencli_list).")] string name,
        [Description("JSON object of arguments, as a string. Use \"{}\" for no args.")] string arguments_json,
        CancellationToken ct = default)
    {
        JsonObject argsObj;
        if (string.IsNullOrWhiteSpace(arguments_json))
        {
            argsObj = new JsonObject();
        }
        else
        {
            try
            {
                // Cap parser depth — untrusted tool-call payload could
                // be deeply nested and blow the default reader limit
                // or excessively allocate.
                var node = JsonNode.Parse(arguments_json,
                    nodeOptions: null,
                    documentOptions: new JsonDocumentOptions { MaxDepth = 64 });
                if (node is JsonObject parsed)
                {
                    argsObj = parsed;
                }
                else
                {
                    return Envelope(false, site, name, "arguments_json must be a JSON object", "BAD_ARGS");
                }
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
            {
                return Envelope(false, site, name, $"arguments_json invalid JSON: {ex.Message}", "BAD_ARGS");
            }
        }

        // SPEC §2.1: PUBLIC adapters work without OpenDia; browser-
        // strategy adapters MUST return {ok:false, error:"opendia-not-
        // connected"} when the extension is absent (NEVER a synthesised
        // fallback). Resolve once, then route.
        AdapterDef? def;
        try
        {
            def = await runtime.Resolve(site, name, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Envelope(false, site, name, ex.Message, "RUNTIME_HOST_ERROR");
        }

        // Surface unknown adapters with a clear envelope BEFORE routing
        // — otherwise a missing site/name silently lands on the stub
        // page and only opencli_run reports the failure deep inside V8.
        if (def is null)
            return Envelope(false, site, name, $"adapter {site}/{name} not registered", "RUNTIME_NOT_FOUND");

        IPage page;
        var strategy = def.Strategy?.Trim().ToLowerInvariant();
        if (def.Browser || strategy is "cookie" or "intercept" or "ui")
        {
            if (bridge is null || !bridge.IsConnected)
                return Envelope(false, site, name, "opendia-not-connected", "BROWSER_NOT_READY");
            page = new OpenDiaPageBridge(bridge);
        }
        else
        {
            page = Phase1StubPage.Instance;
        }
        try
        {
            var resp = await runtime.InvokeAsync(site, name, argsObj, page, ct).ConfigureAwait(false);
            return resp.ToJsonString();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Envelope(false, site, name, ex.Message, "RUNTIME_HOST_ERROR");
        }
    }
}
