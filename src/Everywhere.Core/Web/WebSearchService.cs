using Everywhere.Common;
using Everywhere.Configuration;
using Microsoft.SemanticKernel.Data;
using Settings = Everywhere.Configuration.Settings;

namespace Everywhere.Web;

/// <summary>
/// Provider-agnostic web search entry point exposed for callers outside
/// the chat-plugin pipeline (notably the MCP <c>web_search</c> tool).
///
/// Resolves the connector exactly the way <c>WebPlugin</c> does — same
/// selected-provider lookup, same multi-key pool semantics. Splitting
/// it out keeps WebPlugin's chat-display surface separate from a clean
/// programmatic surface.
/// </summary>
public interface IWebSearchService
{
    Task<IReadOnlyList<TextSearchResult>> SearchAsync(string query, int count, CancellationToken ct = default);
}

public sealed class WebSearchService(Settings settings, IHttpClientFactory httpClientFactory) : IWebSearchService
{
    public async Task<IReadOnlyList<TextSearchResult>> SearchAsync(string query, int count, CancellationToken ct = default)
    {
        var web = settings.Plugin.WebSearchEngine
            ?? throw new InvalidOperationException("Web search settings not initialised.");
        if (web.SelectedProvider is not { } provider)
            throw new InvalidOperationException("Web search engine provider is not selected. Configure one in Main Window > Web Search.");

        using var connector = WebSearchConnectorFactory.Create(provider, httpClientFactory);
        var hits = await connector.SearchAsync(query, count, ct).ConfigureAwait(false);
        return hits as IReadOnlyList<TextSearchResult> ?? hits.ToArray();
    }
}
