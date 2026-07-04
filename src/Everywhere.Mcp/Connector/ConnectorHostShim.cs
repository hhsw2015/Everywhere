using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenCli;
using Microsoft.ClearScript;

namespace Everywhere.Mcp.Connector;

/// <summary>
/// SPEC docs/specs/everywhere-connector.md §6 — JS-side host bridge for the
/// open-connector V8 isolate. Kept minimal: reuse OpenCLI's proven
/// <see cref="HostShim.fetchAsync"/> for HTTP egress, expose one
/// credential lookup, one warn passthrough.
/// </summary>
public sealed class ConnectorHostShim
{
    private readonly HostShim _fetchShim;
    private readonly ICredentialResolver _credentials;
    private readonly TransitFileStore? _transit;
    private readonly Action<string> _onWarn;

    public ConnectorHostShim(HostShim fetchShim, ICredentialResolver credentials, Action<string> onWarn, TransitFileStore? transit = null)
    {
        _fetchShim = fetchShim;
        _credentials = credentials;
        _transit = transit;
        _onWarn = onWarn;
    }

    /// <summary>Bridge to <see cref="HostShim.fetchAsync"/>. The JS bundle's
    /// prepended fetch shim forwards (url, init) directly here so upstream
    /// executor code sees the standard <c>fetch(...)</c> contract.</summary>
    public Task<object> fetchAsync(string url, object? init, CancellationToken ct = default)
        => _fetchShim.fetchAsync(url, init, ct);

    // --- node:crypto bridge (Phase 5) ---------------------------------
    // These delegate to the same helpers OpenCLI HostShim implements —
    // avoids a second SHA/HMAC implementation in the tree.

    public string cryptoHash(string algo, string data, bool isText, string encoding)
        => _fetchShim.cryptoHash(algo, data, isText, encoding);

    public string cryptoHmac(string algo, string key, string data, bool isText, string encoding)
        => _fetchShim.cryptoHmac(algo, key, data, isText, encoding);

    public string cryptoRandomBytes(int n)
        => _fetchShim.cryptoRandomBytes(n);

    public string cryptoUuid()
        => _fetchShim.cryptoUuid();

    // --- transit files (Phase 8) ---------------------------------
    // upstream calls context.transitFiles.create(File). File in V8 is
    // a browser primitive; we accept base64 bytes + name + mimeType from
    // the JS side (bundle wrapper marshals). Return upstream's
    // { fileId, downloadUrl, sizeBytes, name, mimeType } shape.

    public int transitMaxBytes() => _transit?.MaxBytes ?? 0;

    public string transitCreate(string base64Bytes, string name, string mimeType)
    {
        if (_transit is null) throw new InvalidOperationException("transit files not enabled");
        var bytes = Convert.FromBase64String(base64Bytes ?? "");
        return _transit.Create(bytes, name, mimeType).ToJsonString();
    }

    /// <summary>Return the file bytes as base64 plus its name/mimeType.
    /// Used by upstream <c>context.transitFiles.read(fileId)</c>.</summary>
    public string? transitRead(string fileId)
    {
        if (_transit is null || !_transit.TryRead(fileId, out var bytes, out var name, out var mime))
            return null;
        return new JsonObject
        {
            ["base64"] = Convert.ToBase64String(bytes),
            ["sizeBytes"] = bytes.Length,
            ["name"] = name,
            ["mimeType"] = mime,
        }.ToJsonString();
    }

    public bool transitDelete(string fileId) => _transit?.Delete(fileId) ?? false;

    /// <summary>Return the resolved credential for a service as a plain JS
    /// object shaped like upstream's <c>ResolvedCredential</c>, or
    /// <c>undefined</c> when the service has no configured connection.
    /// Upstream <c>require*Credential(...)</c> raises a 401
    /// ProviderRequestError on the undefined case, which surfaces to
    /// the MCP envelope as <c>code: authorization_failed</c>.</summary>
    public object? getCredential(string service)
    {
        if (string.IsNullOrWhiteSpace(service)) return null;
        var cred = _credentials.Resolve(service);
        if (cred is null) return null;
        // Round-trip via JSON so ClearScript materialises the value as a
        // plain JS object (V8 side) instead of a host-object reference —
        // upstream code reads e.g. `cred.apiKey` and expects a real string,
        // not a ScriptEngine host proxy.
        return cred.ToJsonString();
    }

    public void warn(string message) => _onWarn(message);
}

/// <summary>Resolve provider credentials for a given service id. Phase 1
/// implementation reads env vars; Phase 2 will swap to SQLite.</summary>
public interface ICredentialResolver
{
    JsonObject? Resolve(string service);
}

/// <summary>SPEC §7 Phase 1 — read <c>EVERYWHERE_CONNECTOR_&lt;SERVICE&gt;_PAT</c>
/// and surface it as an api_key ResolvedCredential.</summary>
public sealed class EnvironmentCredentialResolver : ICredentialResolver
{
    public JsonObject? Resolve(string service)
    {
        if (string.IsNullOrWhiteSpace(service)) return null;
        var envKey = $"EVERYWHERE_CONNECTOR_{service.ToUpperInvariant()}_PAT";
        var value = Environment.GetEnvironmentVariable(envKey);
        if (string.IsNullOrEmpty(value)) return null;

        return new JsonObject
        {
            ["authType"] = "api_key",
            ["apiKey"] = value,
            ["values"] = new JsonObject { ["apiKey"] = value },
            ["profile"] = new JsonObject
            {
                ["accountId"] = "env",
                ["displayName"] = $"{service} (env PAT)",
                ["grantedScopes"] = new JsonArray(),
            },
            ["metadata"] = new JsonObject(),
        };
    }
}
