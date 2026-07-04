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
