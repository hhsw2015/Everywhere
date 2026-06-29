using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Web;
using Microsoft.SemanticKernel.Data;

namespace Everywhere.Web;

/// <summary>
/// TinyFish search.tinyfish.ai connector.
///
/// API: GET https://api.search.tinyfish.ai?query=&lt;url-encoded&gt;
///   Header: X-API-Key: &lt;key&gt;
///
/// 429 → base WebSearchClient retries once with another key from the pool.
/// Ported from CLIProxyAPIPlus internal/runtime/executor/websearch_tinyfish.go.
/// </summary>
public sealed partial class TinyFishConnector(KeyPool keys, HttpClient httpClient, Uri uri)
    : WebSearchClient<TinyFishConnector.Response>(httpClient, new Range(1, 50), keys)
{
    protected override JsonTypeInfo<Response> JsonTypeInfo => TinyFishJsonSerializerContext.Default.Response;

    protected override HttpRequestMessage CreateSearchRequest(string query, int count)
    {
        // TinyFish's documented endpoint takes the query in the URL; count
        // isn't a real knob there, so we ignore `count` on the wire and
        // truncate client-side (base class applies Take(count)).
        var endpoint = new UriBuilder(uri)
        {
            Query = $"query={HttpUtility.UrlEncode(query)}",
        }.Uri;
        return new HttpRequestMessage(HttpMethod.Get, endpoint)
        {
            Headers =
            {
                { "Accept", "application/json" },
                { "X-API-Key", NextKey() ?? string.Empty },
            },
        };
    }

    [JsonSerializable(typeof(Response))]
    private partial class TinyFishJsonSerializerContext : JsonSerializerContext;

    public sealed class Response : IWebSearchResponse
    {
        [JsonPropertyName("query")] public string? Query { get; init; }
        [JsonPropertyName("results")] public IReadOnlyList<Result>? Results { get; init; }
        [JsonPropertyName("total_results")] public int TotalResults { get; init; }

        public IEnumerable<TextSearchResult> ToResults() =>
            Results?.Select(r => new TextSearchResult(r.Snippet ?? "")
            {
                Name = r.Title,
                Link = r.Url,
            }) ?? [];
    }

    public sealed class Result
    {
        [JsonPropertyName("position")] public int Position { get; init; }
        [JsonPropertyName("site_name")] public string? SiteName { get; init; }
        [JsonPropertyName("title")] public string? Title { get; init; }
        [JsonPropertyName("snippet")] public string? Snippet { get; init; }
        [JsonPropertyName("url")] public string? Url { get; init; }
    }
}
