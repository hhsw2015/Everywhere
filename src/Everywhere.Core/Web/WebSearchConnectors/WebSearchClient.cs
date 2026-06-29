using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.SemanticKernel.Data;

namespace Everywhere.Web;

public interface IWebSearchResponse
{
    IEnumerable<TextSearchResult> ToResults();
}

/// <summary>
/// Base for HTTP-backed web search connectors.
///
/// Two responsibilities collapse here:
///  - JSON request/response shape (subclass overrides
///    <see cref="CreateSearchRequest"/> + <see cref="JsonTypeInfo"/>).
///  - Rate-limit-aware key rotation (a <see cref="KeyPool"/> is passed
///    in; <see cref="NextKey"/> returns the next live key,
///    <see cref="SearchAsync"/> handles 429 / 401 by marking the offending
///    key cool and retrying once with a fresh one).
/// </summary>
public abstract class WebSearchClient<TResponse>(HttpClient httpClient, Range validCountRange, KeyPool keys)
    : IWebSearchEngineConnector where TResponse : class, IWebSearchResponse
{
    protected abstract JsonTypeInfo<TResponse> JsonTypeInfo { get; }

    /// <summary>
    /// Build the per-call HTTP request. Subclasses call <see cref="NextKey"/>
    /// inside this method to inject the auth header — the base class then
    /// remembers which key was used so it can route around 429s.
    /// </summary>
    protected abstract HttpRequestMessage CreateSearchRequest(string query, int count);

    protected virtual Exception? TransformSearchException(Exception exception) => null;

    /// <summary>
    /// Connector-managed key pool. Subclasses call <see cref="NextKey"/> to
    /// fetch the active key; the base class observes it via
    /// <see cref="LastKeyUsed"/> for cooldown bookkeeping.
    /// </summary>
    protected KeyPool Keys => keys;

    /// <summary>
    /// Latest key vended via <see cref="NextKey"/>. Used by the retry path
    /// in <see cref="SearchAsync"/> to mark the offending key on 429 / 401.
    /// Subclasses must call <see cref="NextKey"/>, NOT pluck from
    /// <see cref="Keys"/>.Next directly, so this stays in sync.
    /// </summary>
    protected string? LastKeyUsed { get; private set; }

    protected string? NextKey()
    {
        LastKeyUsed = keys.Next();
        return LastKeyUsed;
    }

    public async Task<IEnumerable<TextSearchResult>> SearchAsync(
        string query,
        int count,
        CancellationToken cancellationToken = default)
    {
        try
        {
            count = Math.Clamp(count, validCountRange.Start.Value, validCountRange.End.Value);

            return await SendOnceAsync(query, count, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (IsRateLimit(ex) && keys.Count > 1)
        {
            // Quota exhausted on one key — mark it cold and retry exactly
            // once. If the retry also 429s we let the exception bubble.
            if (!string.IsNullOrEmpty(LastKeyUsed)) keys.MarkRateLimited(LastKeyUsed);
            return await SendOnceAsync(query, count, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (TransformSearchException(ex) is { } transformed) throw transformed;
            throw;
        }
    }

    private async Task<IEnumerable<TextSearchResult>> SendOnceAsync(
        string query,
        int count,
        CancellationToken cancellationToken)
    {
        var requestMessage = CreateSearchRequest(query, count);
        if (requestMessage.Content is not null)
            await requestMessage.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);

        using var responseMessage = await httpClient.SendAsync(requestMessage, cancellationToken).ConfigureAwait(false);
        if (!responseMessage.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Web Search API returned error ({responseMessage.ReasonPhrase}) {await TryReadErrorContent(responseMessage, cancellationToken)}",
                null,
                responseMessage.StatusCode);
        }

        var response = await responseMessage.Content.ReadFromJsonAsync(JsonTypeInfo, cancellationToken) ??
            throw new HttpRequestException("Web Search API returned null.", null, responseMessage.StatusCode);

        try
        {
            return response.ToResults().Take(count);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to parse the response from the web search engine.", ex);
        }
    }

    private static bool IsRateLimit(HttpRequestException ex) =>
        ex.StatusCode is HttpStatusCode.TooManyRequests
            or HttpStatusCode.Unauthorized
            or HttpStatusCode.PaymentRequired;

    private static async Task<string?> TryReadErrorContent(HttpResponseMessage responseMessage, CancellationToken cancellationToken)
    {
        try
        {
            var content = await responseMessage.Content
                .ReadAsStringAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
            return content.SafeSubstring(0, 1024);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
