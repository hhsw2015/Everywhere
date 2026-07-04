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
    private readonly Action<string> _onWarn;

    public ConnectorHostShim(HostShim fetchShim, ICredentialResolver credentials, Action<string> onWarn)
    {
        _fetchShim = fetchShim;
        _credentials = credentials;
        _onWarn = onWarn;
    }

    /// <summary>Bridge to <see cref="HostShim.fetchAsync"/>. The JS bundle's
    /// prepended fetch shim forwards (url, init) directly here so upstream
    /// executor code sees the standard <c>fetch(...)</c> contract.</summary>
    public Task<object> fetchAsync(string url, object? init, CancellationToken ct = default)
        => _fetchShim.fetchAsync(url, init, ct);

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
