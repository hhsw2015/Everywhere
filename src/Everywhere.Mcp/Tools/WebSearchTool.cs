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
        "🔎 Web search via user's configured Everywhere provider " +
        "(Tavily/Brave/Google/Jina/Searxng/etc.). Multi-key rotating pool. " +
        "Prefer over WebFetch on viewed URLs (~10× token savings). " +
        "Returns title/url/snippet per hit.")]
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
        "📄 Fetch URL as Markdown via Jina r.jina.ai (no API key). " +
        "For pages user isn't viewing — when viewed, prefer browser_snapshot+get_text.")]
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
