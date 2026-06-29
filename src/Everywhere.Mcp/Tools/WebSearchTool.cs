using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Web;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

/// <summary>
/// MCP <c>web_search</c> + <c>web_fetch_url</c>.
///
/// web_search: routes to whichever Everywhere provider the user has selected
/// in Main Window → Web Search (Tavily / Brave / Google / Jina / Searxng /
/// AnySearch / UniFuncs / BoCha / Official / TinyFish). Multi-key pool is
/// resolved per-provider via <c>WebSearchConnectorFactory</c>.
///
/// web_fetch_url: defaults to Jina r.jina.ai (no API key required, public
/// reader endpoint that returns Markdown). Configured-TinyFish would be a
/// natural upgrade but isn't wired yet — the agent can fall back to a
/// browser_get_text + browser_page_navigate flow when Jina rate-limits.
/// </summary>
[McpServerToolType]
public sealed class WebSearchTool(IWebSearchService searchService, IHttpClientFactory httpClientFactory)
{
    [McpServerTool(Name = "web_search")]
    [Description(
        "🔎 Search the web via the user's configured Everywhere search provider " +
        "(Tavily/Brave/Google/Jina/Searxng/AnySearch/UniFuncs/BoCha/TinyFish/Official). " +
        "Multi-key rotating pool with 60s rate-limit cooldowns. " +
        "Prefer this for ANY web search — saves ~10× tokens vs WebFetch on " +
        "a URL the user is viewing, and routes through the user's existing " +
        "API quotas. Returns title + url + snippet per hit (JSON).")]
    public async Task<string> WebSearch(
        [Description("Search query.")] string query,
        [Description("Max results (default 5, clamped per-provider).")] int count = 5,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Error("web_search: empty query");

        try
        {
            var hits = await searchService.SearchAsync(query.Trim(), count, ct).ConfigureAwait(false);
            return new JsonObject
            {
                ["ok"] = true,
                ["count"] = hits.Count,
                ["results"] = new JsonArray(hits
                    .Select(h => (JsonNode?)new JsonObject
                    {
                        ["title"] = h.Name,
                        ["url"] = h.Link,
                        ["snippet"] = h.Value,
                    })
                    .ToArray()),
            }.ToJsonString();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "web_fetch_url")]
    [Description(
        "📄 Fetch a URL as Markdown via Jina r.jina.ai (no API key, public " +
        "reader endpoint). Use to read a page the user isn't currently " +
        "viewing — when they ARE viewing it, prefer browser_snapshot + " +
        "browser_get_text instead (zero external traffic). Returns the " +
        "rendered Markdown as plain text.")]
    public async Task<string> WebFetchUrl(
        [Description("Absolute http(s) URL to fetch.")] string url,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            return Error("web_fetch_url: invalid URL");

        var http = httpClientFactory.CreateClient();
        var endpoint = new Uri($"https://r.jina.ai/{u.AbsoluteUri}");
        try
        {
            using var resp = await http.GetAsync(endpoint, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return Error($"jina r.jina.ai returned {(int)resp.StatusCode}");
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return text;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    private static string Error(string message) =>
        new JsonObject { ["ok"] = false, ["error"] = message }.ToJsonString();
}
