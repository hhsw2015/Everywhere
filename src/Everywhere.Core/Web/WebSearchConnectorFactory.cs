using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Cloud;
using Everywhere.Web;

namespace Everywhere.Web;

/// <summary>
/// Shared connector-construction code. Both <c>WebPlugin</c> (chat) and
/// <c>WebSearchService</c> (MCP) route through here so multi-key pool
/// behaviour stays consistent. Adding a new provider only requires
/// editing this switch + the enum + the connector itself.
/// </summary>
internal static class WebSearchConnectorFactory
{
    public static IWebSearchEngineConnector Create(
        IWebSearchEngineProvider provider,
        IHttpClientFactory httpClientFactory)
    {
        return provider switch
        {
            OfficialWebSearchEngineProvider official => new OfficialConnector(
                httpClientFactory.CreateClient(nameof(ICloudClient)),
                official.Settings),
            OptionalApiKeyWebSearchEngineProvider { Id: WebSearchEngineProviderId.AnySearch } anySearch =>
                new AnySearchConnector(
                    BuildPool(anySearch.ApiKey, anySearch.ExtraApiKeyIds, required: false),
                    httpClientFactory.CreateClient(),
                    EnsureUri(anySearch.EndPoint)),
            ApiKeyWebSearchEngineProvider { Id: WebSearchEngineProviderId.Bocha } bocha =>
                new BoChaConnector(BuildPool(bocha.ApiKey, bocha.ExtraApiKeyIds), httpClientFactory.CreateClient(), EnsureUri(bocha.EndPoint)),
            ApiKeyWebSearchEngineProvider { Id: WebSearchEngineProviderId.Brave } brave =>
                new BraveConnector(BuildPool(brave.ApiKey, brave.ExtraApiKeyIds), httpClientFactory.CreateClient(), EnsureUri(brave.EndPoint)),
            GoogleWebSearchEngineProvider google => new GoogleConnector(
                BuildPool(google.ApiKey, google.ExtraApiKeyIds),
                google.SearchEngineId ?? throw new InvalidOperationException("Google Search Engine ID is not set."),
                httpClientFactory.CreateClient(),
                EnsureUri(google.EndPoint)),
            ApiKeyWebSearchEngineProvider { Id: WebSearchEngineProviderId.Jina } jina =>
                new JinaConnector(BuildPool(jina.ApiKey, jina.ExtraApiKeyIds), httpClientFactory.CreateClient(), EnsureUri(jina.EndPoint)),
            SearXNGWebSearchEngineProvider searXNG =>
                new SearxngConnector(httpClientFactory.CreateClient(), EnsureUri(searXNG.EndPoint)),
            ApiKeyWebSearchEngineProvider { Id: WebSearchEngineProviderId.Tavily } tavily =>
                new TavilyConnector(BuildPool(tavily.ApiKey, tavily.ExtraApiKeyIds), httpClientFactory.CreateClient(), EnsureUri(tavily.EndPoint)),
            ApiKeyWebSearchEngineProvider { Id: WebSearchEngineProviderId.UniFuncs } uniFuncs =>
                new UniFuncsConnector(BuildPool(uniFuncs.ApiKey, uniFuncs.ExtraApiKeyIds), httpClientFactory.CreateClient(), EnsureUri(uniFuncs.EndPoint)),
            ApiKeyWebSearchEngineProvider { Id: WebSearchEngineProviderId.TinyFish } tinyFish =>
                new TinyFishConnector(BuildPool(tinyFish.ApiKey, tinyFish.ExtraApiKeyIds), httpClientFactory.CreateClient(), EnsureUri(tinyFish.EndPoint)),
            _ => throw new NotSupportedException($"Unsupported web search provider: {provider.Id}"),
        };
    }

    private static Uri EnsureUri(Customizable<string> endpoint)
    {
        var s = endpoint?.Value;
        if (string.IsNullOrWhiteSpace(s))
            throw new InvalidOperationException("Web search endpoint is empty.");
        return new Uri(s, UriKind.Absolute);
    }

    private static KeyPool BuildPool(Guid primary, IEnumerable<Guid>? extras, bool required = true)
    {
        var keys = new List<string>();
        if (primary != Guid.Empty && ApiKey.GetKey(primary) is { Length: > 0 } pk) keys.Add(pk);
        if (extras is not null)
        {
            foreach (var id in extras)
            {
                if (id == Guid.Empty || id == primary) continue;
                if (ApiKey.GetKey(id) is { Length: > 0 } sk) keys.Add(sk);
            }
        }
        if (required && keys.Count == 0)
            throw new InvalidOperationException(
                "Web search API key is not set. Configure one in Main Window > Web Search.");
        return new KeyPool(keys);
    }
}
