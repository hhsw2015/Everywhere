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
                    BuildPool(anySearch.ApiKeys, required: false),
                    httpClientFactory.CreateClient(),
                    EnsureUri(anySearch.EndPoint)),
            ApiKeyWebSearchEngineProvider { Id: WebSearchEngineProviderId.Bocha } bocha =>
                new BoChaConnector(BuildPool(bocha.ApiKeys), httpClientFactory.CreateClient(), EnsureUri(bocha.EndPoint)),
            ApiKeyWebSearchEngineProvider { Id: WebSearchEngineProviderId.Brave } brave =>
                new BraveConnector(BuildPool(brave.ApiKeys), httpClientFactory.CreateClient(), EnsureUri(brave.EndPoint)),
            GoogleWebSearchEngineProvider google => new GoogleConnector(
                BuildPool(google.ApiKeys),
                google.SearchEngineId ?? throw new InvalidOperationException("Google Search Engine ID is not set."),
                httpClientFactory.CreateClient(),
                EnsureUri(google.EndPoint)),
            ApiKeyWebSearchEngineProvider { Id: WebSearchEngineProviderId.Jina } jina =>
                new JinaConnector(BuildPool(jina.ApiKeys), httpClientFactory.CreateClient(), EnsureUri(jina.EndPoint)),
            SearXNGWebSearchEngineProvider searXNG =>
                new SearxngConnector(httpClientFactory.CreateClient(), EnsureUri(searXNG.EndPoint)),
            ApiKeyWebSearchEngineProvider { Id: WebSearchEngineProviderId.Tavily } tavily =>
                new TavilyConnector(BuildPool(tavily.ApiKeys), httpClientFactory.CreateClient(), EnsureUri(tavily.EndPoint)),
            ApiKeyWebSearchEngineProvider { Id: WebSearchEngineProviderId.UniFuncs } uniFuncs =>
                new UniFuncsConnector(BuildPool(uniFuncs.ApiKeys), httpClientFactory.CreateClient(), EnsureUri(uniFuncs.EndPoint)),
            ApiKeyWebSearchEngineProvider { Id: WebSearchEngineProviderId.TinyFish } tinyFish =>
                new TinyFishConnector(BuildPool(tinyFish.ApiKeys), httpClientFactory.CreateClient(), EnsureUri(tinyFish.EndPoint)),
            _ => throw new NotSupportedException($"Unsupported web search provider: {provider.Id}"),
        };
    }

    private static Uri EnsureUri(Customizable<string> endpoint)
    {
        if (!Uri.TryCreate(endpoint?.ActualValue, UriKind.Absolute, out var uri) ||
            uri.Scheme is not "http" and not "https")
        {
            throw new InvalidOperationException(
                "Web search endpoint is not a valid absolute http/https URI.");
        }

        // Strip query so per-call request can append its own.
        return new UriBuilder(uri) { Query = string.Empty }.Uri;
    }

    private static KeyPool BuildPool(IEnumerable<ApiKey> source, bool required = true)
    {
        var keys = new List<string>();
        foreach (var k in source)
        {
            if (k.Id == Guid.Empty) continue;
            if (ApiKey.GetKey(k.Id) is { Length: > 0 } secret) keys.Add(secret);
        }
        if (required && keys.Count == 0)
            throw new InvalidOperationException(
                "Web search API key is not set. Configure one in Main Window > Web Search.");
        return new KeyPool(keys);
    }
}
