using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp.Connector;

/// <summary>
/// SPEC docs/specs/everywhere-connector.md §7 Phase 3 — OAuth 2.0
/// authorization-code flow, adapted from upstream oauth-flow-service.ts.
///
/// Flow, from the user's perspective:
/// 1. Configure a client via /api/oauth/configs (client_id, client_secret,
///    redirect_uri = http://127.0.0.1:PORT/api/oauth/callback).
/// 2. Call /api/oauth/authorize/:service — daemon generates state (+ PKCE
///    if the provider requires it), returns the browser-openable URL.
/// 3. User approves in the browser; provider redirects to
///    /api/oauth/callback?code=...&amp;state=...
/// 4. Daemon exchanges the code for tokens, stores an oauth2 credential.
///
/// Loopback-only: the callback endpoint sits behind
/// EverywhereMcpHttpHost.LoopbackOnly, so a hostile external process
/// can't feed us fake callbacks.
/// </summary>
public sealed class OAuthFlowService : IOAuthRefresher
{
    private readonly ConnectorRuntime _runtime;
    private readonly JsonCredentialStore _store;
    private readonly HttpClient _http;
    private readonly ILogger<OAuthFlowService>? _log;

    public OAuthFlowService(
        ConnectorRuntime runtime,
        JsonCredentialStore store,
        HttpClient http,
        ILogger<OAuthFlowService>? log = null)
    {
        _runtime = runtime;
        _store = store;
        _http = http;
        _log = log;
    }

    /// <summary>Build the authorization URL and stash the state.</summary>
    public AuthorizeResult Authorize(string service)
    {
        var manifest = _runtime.ListManifest();
        var svc = manifest.Services.FirstOrDefault(s => string.Equals(s.Service, service, StringComparison.OrdinalIgnoreCase));
        if (svc is null) throw new OAuthException("RUNTIME_NOT_FOUND", $"service '{service}' not in catalog");
        if (!svc.AuthTypes.Contains("oauth2", StringComparer.OrdinalIgnoreCase))
            throw new OAuthException("invalid_input", $"service '{service}' does not support OAuth2");

        var authDef = LoadAuthDefinition(svc.Service, "oauth2");
        var client = _store.GetOAuthClient(service)
                     ?? throw new OAuthException("invalid_input", $"OAuth client not configured for '{service}'. POST /api/oauth/configs first.");

        var clientId = client["clientId"]?.GetValue<string>() ?? "";
        var redirectUri = client["redirectUri"]?.GetValue<string>() ?? "";
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(redirectUri))
            throw new OAuthException("invalid_input", "OAuth client missing clientId or redirectUri");

        var state = GenerateOpaqueString(24);
        string? codeVerifier = null;
        string? codeChallenge = null;
        if (authDef?["pkce"] is JsonObject)
        {
            codeVerifier = GenerateOpaqueString(64);
            codeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        }

        var authorizationUrl = authDef?["authorizationUrl"]?.GetValue<string>()
                               ?? throw new OAuthException("provider_error", "authorizationUrl missing from provider definition");
        var scopes = (authDef?["scopes"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").Where(s => !string.IsNullOrEmpty(s)) ?? Array.Empty<string>();
        var scopeSep = authDef?["scopeSeparator"]?.GetValue<string>() ?? " ";

        var query = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["state"] = state,
        };
        if (scopes.Any()) query["scope"] = string.Join(scopeSep, scopes);
        if (codeChallenge is not null)
        {
            query["code_challenge"] = codeChallenge;
            query["code_challenge_method"] = "S256";
        }
        // Provider-specific static params (e.g. Google's access_type=offline).
        if (authDef?["authorizationParams"] is JsonObject extra)
        {
            foreach (var (k, v) in extra)
                if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) query[k] = s;
        }

        var url = AppendQuery(authorizationUrl, query);
        _store.PutOAuthPending(state, service, codeVerifier);
        _log?.LogInformation("connector oauth: authorize service={Service} state={State}", service, state);
        return new AuthorizeResult(service, url, state, redirectUri);
    }

    /// <summary>Handle the callback: exchange code for tokens, save credential.</summary>
    public async Task<CallbackResult> HandleCallbackAsync(string state, string code, CancellationToken ct = default)
    {
        var (service, codeVerifier) = _store.TakeOAuthPending(state);
        if (service is null)
            throw new OAuthException("invalid_input", "unknown or expired state — start the flow again");

        var manifest = _runtime.ListManifest();
        var svc = manifest.Services.FirstOrDefault(s => string.Equals(s.Service, service, StringComparison.OrdinalIgnoreCase))
                  ?? throw new OAuthException("RUNTIME_NOT_FOUND", $"service '{service}' vanished from catalog mid-flow");
        var authDef = LoadAuthDefinition(svc.Service, "oauth2")
                      ?? throw new OAuthException("provider_error", "oauth2 definition missing");
        var client = _store.GetOAuthClient(service)
                     ?? throw new OAuthException("invalid_input", $"OAuth client for '{service}' vanished mid-flow");

        var clientId = client["clientId"]?.GetValue<string>() ?? "";
        var clientSecret = client["clientSecret"]?.GetValue<string>() ?? "";
        var redirectUri = client["redirectUri"]?.GetValue<string>() ?? "";

        var tokenUrl = authDef["tokenUrl"]?.GetValue<string>()
                       ?? throw new OAuthException("provider_error", "tokenUrl missing from provider definition");

        var authMethod = authDef["tokenEndpointAuthMethod"]?.GetValue<string>() ?? "client_secret_post";
        var format = authDef["tokenRequestFormat"]?.GetValue<string>() ?? "form";

        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
        };
        if (authMethod == "client_secret_post")
        {
            body["client_id"] = clientId;
            body["client_secret"] = clientSecret;
        }
        if (!string.IsNullOrEmpty(codeVerifier))
            body["code_verifier"] = codeVerifier;

        using var req = new HttpRequestMessage(HttpMethod.Post, tokenUrl);
        if (format == "json")
        {
            var jsonBody = new JsonObject();
            foreach (var kv in body) jsonBody[kv.Key] = kv.Value;
            req.Content = new StringContent(jsonBody.ToJsonString(), Encoding.UTF8, "application/json");
        }
        else
        {
            req.Content = new FormUrlEncodedContent(body);
        }
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (authMethod == "client_secret_basic")
        {
            var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new OAuthException("provider_error", $"token exchange failed with {(int)resp.StatusCode}: {raw}");

        JsonObject tokenObj;
        try
        {
            tokenObj = JsonNode.Parse(raw) as JsonObject
                       ?? throw new OAuthException("provider_error", $"token exchange returned non-object body: {raw}");
        }
        catch (JsonException)
        {
            // GitHub with Accept: application/json returns JSON, but if the
            // provider returned x-www-form-urlencoded (odd-but-legal),
            // parse that instead.
            tokenObj = ParseFormEncoded(raw);
        }

        // Some providers wrap the token payload (e.g. Feishu, Baidu). Follow
        // tokenResponseEnvelope.dataField if configured.
        if (authDef["tokenResponseEnvelope"] is JsonObject envelope
            && envelope["dataField"]?.GetValue<string>() is string dataField
            && tokenObj[dataField] is JsonObject inner)
        {
            tokenObj = inner;
        }

        var accessToken = tokenObj["access_token"]?.GetValue<string>()
                          ?? throw new OAuthException("provider_error", $"token response missing access_token: {raw}");
        var tokenType = tokenObj["token_type"]?.GetValue<string>() ?? "Bearer";
        var refreshToken = tokenObj["refresh_token"]?.GetValue<string>();
        var expiresIn = tokenObj["expires_in"]?.GetValue<double?>();
        string? expiresAt = expiresIn is > 0
            ? DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value).ToString("O")
            : null;
        var scopeString = tokenObj["scope"]?.GetValue<string>();
        var scopes = string.IsNullOrEmpty(scopeString)
            ? Array.Empty<string>()
            : scopeString.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);

        _store.SetOAuth2Credential(
            service: service,
            accessToken: accessToken,
            tokenType: tokenType,
            refreshToken: refreshToken,
            expiresAt: expiresAt,
            grantedScopes: scopes,
            displayName: $"{service} OAuth",
            metadata: tokenObj);

        _log?.LogInformation("connector oauth: callback service={Service} state={State} scopes={ScopeCount}",
            service, state, scopes.Length);
        return new CallbackResult(service, tokenType, scopes);
    }

    /// <summary>Refresh an OAuth access token using the stored refresh_token.
    /// Called opportunistically by ConnectorRuntime.InvokeAsync before
    /// handing the credential to a JS executor. Returns true when the
    /// token was refreshed, false when refresh is unavailable/failed —
    /// caller then lets the executor hit the provider with the possibly-
    /// expired token so upstream returns a real 401 to the agent.</summary>
    public async Task<bool> TryRefreshAsync(string service, CancellationToken ct = default)
    {
        var cred = _store.Resolve(service);
        if (cred is null) return false;
        if (cred["authType"]?.GetValue<string>() != "oauth2") return false;
        var refreshToken = cred["refreshToken"]?.GetValue<string>();
        if (string.IsNullOrEmpty(refreshToken)) return false;

        var manifest = _runtime.ListManifest();
        var svc = manifest.Services.FirstOrDefault(s => string.Equals(s.Service, service, StringComparison.OrdinalIgnoreCase));
        if (svc is null) return false;
        var authDef = LoadAuthDefinition(service, "oauth2");
        if (authDef is null) return false;

        var client = _store.GetOAuthClient(service);
        if (client is null) return false;

        var tokenUrl = authDef["refreshTokenUrl"]?.GetValue<string>()
                       ?? authDef["tokenUrl"]?.GetValue<string>();
        if (string.IsNullOrEmpty(tokenUrl)) return false;

        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        };
        var authMethod = authDef["tokenEndpointAuthMethod"]?.GetValue<string>() ?? "client_secret_post";
        var clientId = client["clientId"]?.GetValue<string>() ?? "";
        var clientSecret = client["clientSecret"]?.GetValue<string>() ?? "";
        if (authMethod == "client_secret_post")
        {
            body["client_id"] = clientId;
            body["client_secret"] = clientSecret;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(body),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (authMethod == "client_secret_basic")
        {
            var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log?.LogWarning("connector oauth refresh: service={Service} status={Status} body={Body}",
                    service, (int)resp.StatusCode, Truncate(raw, 200));
                return false;
            }
            JsonObject tokenObj;
            try { tokenObj = JsonNode.Parse(raw) as JsonObject ?? ParseFormEncoded(raw); }
            catch (JsonException) { tokenObj = ParseFormEncoded(raw); }
            if (authDef["tokenResponseEnvelope"] is JsonObject envelope
                && envelope["dataField"]?.GetValue<string>() is string dataField
                && tokenObj[dataField] is JsonObject inner)
            {
                tokenObj = inner;
            }
            var accessToken = tokenObj["access_token"]?.GetValue<string>();
            if (string.IsNullOrEmpty(accessToken)) return false;
            var tokenType = tokenObj["token_type"]?.GetValue<string>() ?? "Bearer";
            // Some providers rotate the refresh token; keep the old one
            // if not present in the response.
            var newRefresh = tokenObj["refresh_token"]?.GetValue<string>() ?? refreshToken;
            var expiresIn = tokenObj["expires_in"]?.GetValue<double?>();
            string? expiresAt = expiresIn is > 0
                ? DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value).ToString("O")
                : null;
            var scopeString = tokenObj["scope"]?.GetValue<string>();
            var scopes = string.IsNullOrEmpty(scopeString)
                ? (cred["profile"]?["grantedScopes"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").ToArray()
                  ?? Array.Empty<string>()
                : scopeString.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            _store.SetOAuth2Credential(
                service: service,
                accessToken: accessToken,
                tokenType: tokenType,
                refreshToken: newRefresh,
                expiresAt: expiresAt,
                grantedScopes: scopes,
                displayName: cred["profile"]?["displayName"]?.GetValue<string>(),
                metadata: tokenObj);
            _log?.LogInformation("connector oauth refresh: service={Service} ok expiresAt={ExpiresAt}", service, expiresAt);
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "connector oauth refresh failed: {Service}", service);
            return false;
        }
    }

    /// <summary>Return true when the stored credential has an expiresAt
    /// less than <paramref name="marginSeconds"/> from now — the runtime
    /// uses this to decide whether to preemptively refresh before an
    /// executor call.</summary>
    public bool NeedsRefresh(string service, int marginSeconds = 60)
    {
        var cred = _store.Resolve(service);
        if (cred is null) return false;
        if (cred["authType"]?.GetValue<string>() != "oauth2") return false;
        var expiresAt = cred["expiresAt"]?.GetValue<string>();
        if (string.IsNullOrEmpty(expiresAt)) return false;
        if (!DateTimeOffset.TryParse(expiresAt, out var ts)) return false;
        return ts - DateTimeOffset.UtcNow < TimeSpan.FromSeconds(marginSeconds);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...";

    // SPEC §7 Phase 3 + Phase 6 — curated OAuth provider definitions.
    // Keys must match the provider service name so `LoadAuthDefinition`
    // can resolve them directly. Extend as more OAuth providers land in
    // the bundle. Each entry mirrors the shape open-connector's
    // definition.ts describes.
    private JsonObject? LoadAuthDefinition(string service, string authType)
    {
        // The manifest stores full ActionDefinitions but not the top-level
        // provider auth[] array. Read it fresh from the definition file
        // via the runtime's manifest — currently we vendor it in the
        // bundle but not the manifest. Fallback: re-read the definition
        // from disk. For Phase 3.5 we hand-fetch from a curated map so
        // we don't ship the whole provider definition into the manifest.
        //
        // Curated map covers the 3 OAuth providers currently in the
        // Phase 2 bundle: github, linear (both dual auth). Extend as
        // more OAuth providers land.
        var curated = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase)
        {
            ["github"] = new JsonObject
            {
                ["type"] = "oauth2",
                ["authorizationUrl"] = "https://github.com/login/oauth/authorize",
                ["tokenUrl"] = "https://github.com/login/oauth/access_token",
                ["scopes"] = new JsonArray("repo", "read:user", "user:email"),
                ["tokenEndpointAuthMethod"] = "client_secret_post",
            },
            ["linear"] = new JsonObject
            {
                ["type"] = "oauth2",
                ["authorizationUrl"] = "https://linear.app/oauth/authorize",
                ["tokenUrl"] = "https://api.linear.app/oauth/token",
                ["scopes"] = new JsonArray("read", "write"),
                ["tokenEndpointAuthMethod"] = "client_secret_post",
            },
            // Google OAuth: covers Drive/Gmail/Calendar/etc. — user
            // narrows via authorization_params if needed. `offline`
            // access_type is required to receive a refresh_token.
            ["google"] = new JsonObject
            {
                ["type"] = "oauth2",
                ["authorizationUrl"] = "https://accounts.google.com/o/oauth2/v2/auth",
                ["tokenUrl"] = "https://oauth2.googleapis.com/token",
                ["scopes"] = new JsonArray("openid", "email", "profile"),
                ["tokenEndpointAuthMethod"] = "client_secret_post",
                ["authorizationParams"] = new JsonObject
                {
                    ["access_type"] = "offline",
                    ["prompt"] = "consent",
                },
            },
            ["slack"] = new JsonObject
            {
                ["type"] = "oauth2",
                ["authorizationUrl"] = "https://slack.com/oauth/v2/authorize",
                ["tokenUrl"] = "https://slack.com/api/oauth.v2.access",
                ["scopes"] = new JsonArray("chat:write", "channels:read", "users:read"),
                ["tokenEndpointAuthMethod"] = "client_secret_post",
                // Slack wraps the token payload in { ok: true, authed_user, ... }
                // — but access_token sits at the top level. No envelope
                // needed for the app token; user-scoped tokens live under
                // authed_user{}.
            },
            ["notion"] = new JsonObject
            {
                ["type"] = "oauth2",
                ["authorizationUrl"] = "https://api.notion.com/v1/oauth/authorize",
                ["tokenUrl"] = "https://api.notion.com/v1/oauth/token",
                ["scopes"] = new JsonArray(),
                ["tokenEndpointAuthMethod"] = "client_secret_basic",
                ["authorizationParams"] = new JsonObject
                {
                    ["owner"] = "user",
                },
            },
            ["hubspot"] = new JsonObject
            {
                ["type"] = "oauth2",
                ["authorizationUrl"] = "https://app.hubspot.com/oauth/authorize",
                ["tokenUrl"] = "https://api.hubapi.com/oauth/v1/token",
                ["scopes"] = new JsonArray("crm.objects.contacts.read", "crm.objects.contacts.write"),
                ["tokenEndpointAuthMethod"] = "client_secret_post",
            },
        };
        return curated.TryGetValue(service, out var def) ? def : null;
    }

    private static string GenerateOpaqueString(int byteLen)
    {
        var buf = new byte[byteLen];
        RandomNumberGenerator.Fill(buf);
        return Base64Url(buf);
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string AppendQuery(string url, Dictionary<string, string?> query)
    {
        var sb = new StringBuilder(url);
        sb.Append(url.Contains('?') ? '&' : '?');
        var first = true;
        foreach (var (k, v) in query)
        {
            if (v is null) continue;
            if (!first) sb.Append('&');
            first = false;
            sb.Append(HttpUtility.UrlEncode(k));
            sb.Append('=');
            sb.Append(HttpUtility.UrlEncode(v));
        }
        return sb.ToString();
    }

    private static JsonObject ParseFormEncoded(string raw)
    {
        var obj = new JsonObject();
        foreach (var pair in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            var k = HttpUtility.UrlDecode(pair[..eq]);
            var v = HttpUtility.UrlDecode(pair[(eq + 1)..]);
            obj[k] = v;
        }
        return obj;
    }
}

public sealed class OAuthException : Exception
{
    public string Code { get; }
    public OAuthException(string code, string message) : base(message) { Code = code; }
}

public sealed record AuthorizeResult(string Service, string Url, string State, string RedirectUri);

public sealed record CallbackResult(string Service, string TokenType, IReadOnlyList<string> Scopes);
