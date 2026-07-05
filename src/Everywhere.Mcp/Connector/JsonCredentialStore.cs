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
/// Phase 6: secret fields (apiKey, clientSecret, accessToken, refreshToken)
/// are encrypted with AES-256-GCM using a keyring stashed next to
/// connections.json. Migration is transparent — legacy plaintext values
/// are decrypted as-is (see <see cref="CredentialEncryptor.Decrypt"/>)
/// and re-encrypted on next write.
/// </summary>
public sealed class JsonCredentialStore : INamedCredentialResolver
{
    // Fields inside the JSON document that hold user-provided secrets.
    // Any value under one of these keys (anywhere in the object tree) is
    // wrapped/unwrapped via CredentialEncryptor when serialising to /
    // deserialising from disk.
    private static readonly HashSet<string> SecretFields = new(StringComparer.Ordinal)
    {
        "apiKey",
        "clientSecret",
        "accessToken",
        "refreshToken",
    };

    private readonly string _filePath;
    private readonly CredentialEncryptor _encryptor;
    private readonly object _fileLock = new();

    public JsonCredentialStore(string? overridePath = null, CredentialEncryptor? encryptor = null)
    {
        _filePath = overridePath ?? DefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath) ?? ".");
        _encryptor = encryptor ?? CredentialEncryptor.LoadOrCreate(
            Path.Combine(Path.GetDirectoryName(_filePath) ?? ".", "keyring.bin"));
    }

    public static string DefaultPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".everywhere", "connector", "connections.json");
    }

    public JsonObject? Resolve(string service)
        => ResolveNamed(service, connectionName: null);

    /// <summary>Resolve a specific connection by name. Passing null or
    /// empty resolves the default connection (bare `service` key).
    /// SPEC Phase 12 — multiple named connections per service.</summary>
    public JsonObject? ResolveNamed(string service, string? connectionName)
    {
        if (string.IsNullOrWhiteSpace(service)) return null;
        var doc = ReadDoc();
        var conns = doc["connections"] as JsonObject;
        if (conns is null) return null;
        var key = MakeKey(service, connectionName);
        var entry = conns[key] as JsonObject;
        if (entry is null) return null;
        return entry.DeepClone() as JsonObject;
    }

    /// <summary>Split a stored key back into (service, connectionName?).
    /// Bare `service` returns (service, null); `service:name` returns
    /// (service, name).</summary>
    public static (string Service, string? ConnectionName) SplitKey(string key)
    {
        var colon = key.IndexOf(':');
        if (colon <= 0) return (key, null);
        return (key.Substring(0, colon), key.Substring(colon + 1));
    }

    private static string MakeKey(string service, string? connectionName)
    {
        if (string.IsNullOrEmpty(connectionName)) return service;
        // The stored key uses ':' as service/connection separator; a
        // colon inside connectionName would break SplitKey. Reject at
        // the store boundary — MCP tools already normalize+reject too,
        // but defense in depth.
        if (connectionName.Contains(':'))
            throw new ArgumentException("connectionName cannot contain ':' (reserved key separator)", nameof(connectionName));
        return $"{service}:{connectionName}";
    }

    public IReadOnlyList<ConnectionSummary> List()
    {
        var doc = ReadDoc();
        var conns = doc["connections"] as JsonObject;
        if (conns is null) return Array.Empty<ConnectionSummary>();
        var list = new List<ConnectionSummary>();
        foreach (var (key, node) in conns)
        {
            if (node is not JsonObject entry) continue;
            var authType = entry["authType"]?.GetValue<string>() ?? "unknown";
            var profile = entry["profile"] as JsonObject;
            var (svc, connName) = SplitKey(key);
            // Fallback DisplayName: keep the composite key out of the
            // user-facing label so a caller listing connections gets
            // "github" for the default and the ConnectionName field to
            // differentiate — not a leaky "github:work" string mashed
            // into DisplayName. Callers rendering a summary can format
            // service + connectionName themselves.
            var fallbackLabel = string.IsNullOrEmpty(connName) ? svc : $"{svc} ({connName})";
            list.Add(new ConnectionSummary(
                Service: svc,
                AuthType: authType,
                DisplayName: profile?["displayName"]?.GetValue<string>() ?? fallbackLabel,
                AccountId: profile?["accountId"]?.GetValue<string>() ?? "",
                ConnectionName: connName));
        }
        return list;
    }

    /// <summary>Store an api_key credential. Overwrites any existing
    /// connection for the same (service, connectionName) tuple.
    /// connectionName defaults to the service's default connection.</summary>
    public void SetApiKey(string service, string apiKey, string? displayName = null, string? connectionName = null)
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
                ["displayName"] = displayName ?? (string.IsNullOrEmpty(connectionName)
                    ? $"{service} api key"
                    : $"{service}:{connectionName} api key"),
                ["grantedScopes"] = new JsonArray(),
            },
            ["metadata"] = new JsonObject(),
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        var storeKey = MakeKey(service, connectionName);
        Mutate(doc =>
        {
            var conns = doc["connections"] as JsonObject;
            if (conns is null)
            {
                conns = new JsonObject();
                doc["connections"] = conns;
            }
            conns[storeKey] = entry;
        });
    }

    public bool Delete(string service) => DeleteNamed(service, connectionName: null);

    public bool DeleteNamed(string service, string? connectionName)
    {
        if (string.IsNullOrWhiteSpace(service)) return false;
        var storeKey = MakeKey(service, connectionName);
        var removed = false;
        Mutate(doc =>
        {
            var conns = doc["connections"] as JsonObject;
            if (conns is not null && conns.ContainsKey(storeKey))
            {
                conns.Remove(storeKey);
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
                if (JsonNode.Parse(raw) is JsonObject obj)
                {
                    // Walk the tree and decrypt every SecretFields hit
                    // in-place so callers work with plaintext values.
                    WalkAndDecrypt(obj);
                    return obj;
                }
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
            // Encrypt just before write — the in-memory doc keeps
            // plaintext so a subsequent ReadDoc/Mutate in the same
            // process doesn't double-encrypt.
            var toWrite = doc.DeepClone() as JsonObject ?? new JsonObject();
            WalkAndEncrypt(toWrite);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, toWrite.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(_filePath))
            {
                File.Replace(tmp, _filePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, _filePath);
            }
            try
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch { /* best-effort */ }
        }
    }

    private void WalkAndDecrypt(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(kv => kv.Key).ToArray())
            {
                var value = obj[key];
                if (SecretFields.Contains(key) && value is JsonValue jv && jv.TryGetValue<string>(out var s))
                {
                    try { obj[key] = _encryptor.Decrypt(s); }
                    catch (InvalidOperationException) { obj[key] = ""; }
                }
                else if (value is JsonObject or JsonArray)
                {
                    WalkAndDecrypt(value);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is not null) WalkAndDecrypt(item);
            }
        }
    }

    private void WalkAndEncrypt(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(kv => kv.Key).ToArray())
            {
                var value = obj[key];
                if (SecretFields.Contains(key) && value is JsonValue jv && jv.TryGetValue<string>(out var s))
                {
                    if (!_encryptor.IsEncrypted(s)) obj[key] = _encryptor.Encrypt(s);
                }
                else if (value is JsonObject or JsonArray)
                {
                    WalkAndEncrypt(value);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is not null) WalkAndEncrypt(item);
            }
        }
    }
}

/// <summary>SPEC §7 — chain multiple credential resolvers. First non-null
/// wins. Used to layer env vars over the JSON store: env for CI/dev
/// override, JSON for daily use.</summary>
public sealed class ChainedCredentialResolver : INamedCredentialResolver
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

    public JsonObject? ResolveNamed(string service, string? connectionName)
    {
        if (string.IsNullOrEmpty(connectionName)) return Resolve(service);
        foreach (var link in _links)
        {
            if (link is INamedCredentialResolver named)
            {
                var hit = named.ResolveNamed(service, connectionName);
                if (hit is not null) return hit;
            }
            // Non-named links (env vars) don't support alternate connections;
            // skip them for named lookups so a stray env var can't shadow
            // a specifically-named store connection.
        }
        // Fallback to the default connection when no chain link had the
        // named entry — matches ConnectorHostShim.getCredential's
        // graceful-fallback behaviour so both call paths (through the
        // shim and direct) agree.
        return Resolve(service);
    }
}

public sealed record ConnectionSummary(
    string Service,
    string AuthType,
    string DisplayName,
    string AccountId,
    string? ConnectionName = null);

public sealed record OAuthClientSummary(
    string Service,
    string ClientId,
    bool HasSecret,
    string RedirectUri);
