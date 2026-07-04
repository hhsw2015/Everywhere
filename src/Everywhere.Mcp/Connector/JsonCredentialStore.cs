using System.Text.Json;
using System.Text.Json.Nodes;

namespace Everywhere.Mcp.Connector;

/// <summary>
/// SPEC docs/specs/everywhere-connector.md §7 Phase 2 — persistent
/// credential store backed by a single JSON file under
/// <c>~/.everywhere/connector/connections.json</c>.
///
/// Single-writer semantics. All ops read the file fresh, mutate, atomic
/// rename back — safe for the common case (one daemon process). No
/// concurrent writers.
///
/// Plaintext for now. Phase 2.5 wraps values with DPAPI on Windows /
/// keychain on macOS / documented plaintext on Linux (matches how
/// Everywhere already handles LLM API keys).
/// </summary>
public sealed class JsonCredentialStore : ICredentialResolver
{
    private readonly string _filePath;
    private readonly object _fileLock = new();

    public JsonCredentialStore(string? overridePath = null)
    {
        _filePath = overridePath ?? DefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath) ?? ".");
    }

    public static string DefaultPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".everywhere", "connector", "connections.json");
    }

    public JsonObject? Resolve(string service)
    {
        if (string.IsNullOrWhiteSpace(service)) return null;
        var doc = ReadDoc();
        var conns = doc["connections"] as JsonObject;
        if (conns is null) return null;
        var entry = conns[service] as JsonObject;
        if (entry is null) return null;
        // Return a deep clone — caller may mutate, and doc is our
        // single-writer state.
        return entry.DeepClone() as JsonObject;
    }

    public IReadOnlyList<ConnectionSummary> List()
    {
        var doc = ReadDoc();
        var conns = doc["connections"] as JsonObject;
        if (conns is null) return Array.Empty<ConnectionSummary>();
        var list = new List<ConnectionSummary>();
        foreach (var (svc, node) in conns)
        {
            if (node is not JsonObject entry) continue;
            var authType = entry["authType"]?.GetValue<string>() ?? "unknown";
            var profile = entry["profile"] as JsonObject;
            list.Add(new ConnectionSummary(
                Service: svc,
                AuthType: authType,
                DisplayName: profile?["displayName"]?.GetValue<string>() ?? svc,
                AccountId: profile?["accountId"]?.GetValue<string>() ?? ""));
        }
        return list;
    }

    /// <summary>Store an api_key credential. Overwrites any existing
    /// connection for the same service. Profile is optional — falls
    /// back to a generic label when missing.</summary>
    public void SetApiKey(string service, string apiKey, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(service)) throw new ArgumentException("service is required", nameof(service));
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("apiKey is required", nameof(apiKey));

        var entry = new JsonObject
        {
            ["authType"] = "api_key",
            ["apiKey"] = apiKey,
            ["values"] = new JsonObject { ["apiKey"] = apiKey },
            ["profile"] = new JsonObject
            {
                ["accountId"] = "user",
                ["displayName"] = displayName ?? $"{service} api key",
                ["grantedScopes"] = new JsonArray(),
            },
            ["metadata"] = new JsonObject(),
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        Mutate(doc =>
        {
            var conns = doc["connections"] as JsonObject;
            if (conns is null)
            {
                conns = new JsonObject();
                doc["connections"] = conns;
            }
            conns[service] = entry;
        });
    }

    public bool Delete(string service)
    {
        if (string.IsNullOrWhiteSpace(service)) return false;
        var removed = false;
        Mutate(doc =>
        {
            var conns = doc["connections"] as JsonObject;
            if (conns is not null && conns.ContainsKey(service))
            {
                conns.Remove(service);
                removed = true;
            }
        });
        return removed;
    }

    // -------- OAuth client config (Phase 3.5) --------
    //
    // Per SPEC §7 Phase 3, OAuth-based providers need per-service client
    // configuration BEFORE any auth flow can start (client_id / secret /
    // optional custom clientConfigFields the provider defines). Stored in
    // the same JSON doc under `oauthClients` so a single file backs both
    // credentials and their pre-auth config.

    public JsonObject? GetOAuthClient(string service)
    {
        var doc = ReadDoc();
        var clients = doc["oauthClients"] as JsonObject;
        return clients?[service]?.DeepClone() as JsonObject;
    }

    public IReadOnlyList<OAuthClientSummary> ListOAuthClients()
    {
        var doc = ReadDoc();
        var clients = doc["oauthClients"] as JsonObject;
        if (clients is null) return Array.Empty<OAuthClientSummary>();
        var list = new List<OAuthClientSummary>();
        foreach (var (svc, node) in clients)
        {
            if (node is not JsonObject entry) continue;
            list.Add(new OAuthClientSummary(
                Service: svc,
                ClientId: entry["clientId"]?.GetValue<string>() ?? "",
                HasSecret: !string.IsNullOrEmpty(entry["clientSecret"]?.GetValue<string>()),
                RedirectUri: entry["redirectUri"]?.GetValue<string>() ?? ""));
        }
        return list;
    }

    public void SetOAuthClient(string service, string clientId, string? clientSecret, string redirectUri, JsonObject? extra = null)
    {
        if (string.IsNullOrWhiteSpace(service)) throw new ArgumentException("service required", nameof(service));
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("clientId required", nameof(clientId));
        if (string.IsNullOrWhiteSpace(redirectUri)) throw new ArgumentException("redirectUri required", nameof(redirectUri));

        var entry = new JsonObject
        {
            ["clientId"] = clientId,
            ["clientSecret"] = clientSecret ?? "",
            ["redirectUri"] = redirectUri,
            ["extra"] = extra ?? new JsonObject(),
            ["updatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        Mutate(doc =>
        {
            var clients = doc["oauthClients"] as JsonObject;
            if (clients is null)
            {
                clients = new JsonObject();
                doc["oauthClients"] = clients;
            }
            clients[service] = entry;
        });
    }

    public bool DeleteOAuthClient(string service)
    {
        if (string.IsNullOrWhiteSpace(service)) return false;
        var removed = false;
        Mutate(doc =>
        {
            var clients = doc["oauthClients"] as JsonObject;
            if (clients is not null && clients.ContainsKey(service))
            {
                clients.Remove(service);
                removed = true;
            }
        });
        return removed;
    }

    // -------- OAuth pending flows (state → service map) --------

    public void PutOAuthPending(string state, string service, string? codeVerifier)
    {
        if (string.IsNullOrWhiteSpace(state)) throw new ArgumentException("state required", nameof(state));
        Mutate(doc =>
        {
            var pending = doc["oauthPending"] as JsonObject;
            if (pending is null)
            {
                pending = new JsonObject();
                doc["oauthPending"] = pending;
            }
            // Reap entries older than 15 minutes so a crashed browser
            // session doesn't leak state forever.
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-15);
            var stale = new List<string>();
            foreach (var (k, v) in pending)
            {
                if (v is JsonObject entry
                    && DateTimeOffset.TryParse(entry["createdAt"]?.GetValue<string>(), out var ts)
                    && ts < cutoff) stale.Add(k);
            }
            foreach (var k in stale) pending.Remove(k);
            pending[state] = new JsonObject
            {
                ["service"] = service,
                ["codeVerifier"] = codeVerifier ?? "",
                ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
            };
        });
    }

    public (string? Service, string? CodeVerifier) TakeOAuthPending(string state)
    {
        if (string.IsNullOrWhiteSpace(state)) return (null, null);
        string? service = null;
        string? verifier = null;
        Mutate(doc =>
        {
            var pending = doc["oauthPending"] as JsonObject;
            if (pending is null) return;
            if (pending[state] is JsonObject entry)
            {
                service = entry["service"]?.GetValue<string>();
                verifier = entry["codeVerifier"]?.GetValue<string>();
                pending.Remove(state);
            }
        });
        return (service, verifier);
    }

    // -------- OAuth2 credential storage (post-token-exchange) --------

    public void SetOAuth2Credential(
        string service,
        string accessToken,
        string tokenType,
        string? refreshToken,
        string? expiresAt,
        IReadOnlyList<string> grantedScopes,
        string? displayName,
        JsonObject? metadata = null)
    {
        if (string.IsNullOrWhiteSpace(service)) throw new ArgumentException("service required", nameof(service));
        if (string.IsNullOrWhiteSpace(accessToken)) throw new ArgumentException("accessToken required", nameof(accessToken));

        var scopeArr = new JsonArray();
        foreach (var s in grantedScopes) scopeArr.Add((JsonNode)JsonValue.Create(s ?? "")!);

        var entry = new JsonObject
        {
            ["authType"] = "oauth2",
            ["accessToken"] = accessToken,
            ["tokenType"] = tokenType ?? "Bearer",
            ["refreshToken"] = refreshToken,
            ["expiresAt"] = expiresAt,
            ["profile"] = new JsonObject
            {
                ["accountId"] = "oauth-user",
                ["displayName"] = displayName ?? $"{service} (OAuth)",
                ["grantedScopes"] = scopeArr,
            },
            ["metadata"] = metadata ?? new JsonObject(),
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        Mutate(doc =>
        {
            var conns = doc["connections"] as JsonObject;
            if (conns is null)
            {
                conns = new JsonObject();
                doc["connections"] = conns;
            }
            conns[service] = entry;
        });
    }

    private JsonObject ReadDoc()
    {
        lock (_fileLock)
        {
            if (!File.Exists(_filePath)) return new JsonObject { ["connections"] = new JsonObject() };
            try
            {
                var raw = File.ReadAllText(_filePath);
                if (JsonNode.Parse(raw) is JsonObject obj) return obj;
            }
            catch { /* fall through to fresh doc */ }
            return new JsonObject { ["connections"] = new JsonObject() };
        }
    }

    private void Mutate(Action<JsonObject> mutator)
    {
        lock (_fileLock)
        {
            var doc = ReadDoc();
            mutator(doc);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            // Atomic rename to protect against half-written files.
            if (File.Exists(_filePath))
            {
                File.Replace(tmp, _filePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, _filePath);
            }
            // Perms: 0600 on Unix. .NET's File.SetUnixFileMode handles it
            // gracefully on Windows (no-op).
            try
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch { /* best-effort */ }
        }
    }
}

/// <summary>SPEC §7 — chain multiple credential resolvers. First non-null
/// wins. Used to layer env vars over the JSON store: env for CI/dev
/// override, JSON for daily use.</summary>
public sealed class ChainedCredentialResolver : ICredentialResolver
{
    private readonly IReadOnlyList<ICredentialResolver> _links;

    public ChainedCredentialResolver(params ICredentialResolver[] links)
    {
        _links = links.ToArray();
    }

    public JsonObject? Resolve(string service)
    {
        foreach (var link in _links)
        {
            var hit = link.Resolve(service);
            if (hit is not null) return hit;
        }
        return null;
    }
}

public sealed record ConnectionSummary(
    string Service,
    string AuthType,
    string DisplayName,
    string AccountId);

public sealed record OAuthClientSummary(
    string Service,
    string ClientId,
    bool HasSecret,
    string RedirectUri);
