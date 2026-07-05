using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Mcp.Connector;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

/// <summary>
/// SPEC docs/specs/everywhere-connector.md §8 — three-tool MCP surface for
/// the open-connector provider bundle. Mirrors <see cref="OpenCliTools"/>
/// structurally: no-arg listing collapses to a provider index, drill-down
/// via <c>service</c>, fuzzy <c>query</c>, and one execution path.
/// </summary>
[McpServerToolType]
public sealed class ConnectorTools
{
    private readonly ConnectorRuntime _runtime;
    private readonly JsonCredentialStore _store;

    public ConnectorTools(ConnectorRuntime runtime, JsonCredentialStore store)
    {
        _runtime = runtime;
        _store = store;
    }

    private static string Envelope(bool ok, string? service, string? name, string? error, string? code, JsonNode? data = null)
    {
        var o = new JsonObject
        {
            ["schema_version"] = "1",
            ["ok"] = ok,
        };
        if (service is not null) o["service"] = service;
        if (name is not null) o["name"] = name;
        if (error is not null) o["error"] = error;
        if (code is not null) o["code"] = code;
        if (data is not null) o["data"] = data;
        return o.ToJsonString();
    }

    [McpServerTool(Name = "connector_list")]
    [Description(
        "List SaaS providers integrated via open-connector. " +
        "No args → provider index (name + action count + categories). " +
        "service=X → drill into one provider's actions. " +
        "query=X → fuzzy search across all providers (cap 60). " +
        "Pair with connector_describe for schemas, connector_run to execute. " +
        "Prefer this over connector_run when unsure which action fits.")]
    public string ConnectorList(
        [Description("Optional service filter (e.g. \"github\").")] string? service = null,
        [Description("Optional case-insensitive substring match on service/action name/description. Cap 60.")] string? query = null,
        CancellationToken ct = default)
    {
        try
        {
            var manifest = _runtime.ListManifest();

            // Drill-down
            if (!string.IsNullOrWhiteSpace(service))
            {
                var svc = manifest.Services.FirstOrDefault(s => string.Equals(s.Service, service.Trim(), StringComparison.OrdinalIgnoreCase));
                if (svc is null)
                    return Envelope(false, service, null, $"service '{service}' not in catalog", "RUNTIME_NOT_FOUND");

                var arr = new JsonArray();
                foreach (var a in svc.Actions)
                {
                    arr.Add(new JsonObject
                    {
                        ["id"] = a.Id,
                        ["name"] = a.Name,
                        ["description"] = a.Description,
                        ["requiredScopes"] = new JsonArray(a.RequiredScopes.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
                    });
                }

                return new JsonObject
                {
                    ["schema_version"] = "1",
                    ["ok"] = true,
                    ["service"] = svc.Service,
                    ["displayName"] = svc.DisplayName,
                    ["categories"] = new JsonArray(svc.Categories.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
                    ["authTypes"] = new JsonArray(svc.AuthTypes.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
                    ["homepageUrl"] = svc.HomepageUrl,
                    ["actions"] = arr,
                    ["upstream_sha"] = manifest.UpstreamSha,
                }.ToJsonString();
            }

            // Fuzzy query
            if (!string.IsNullOrWhiteSpace(query))
            {
                const int Cap = 60;
                var q = query.Trim();
                var all = new List<JsonObject>();
                foreach (var svc in manifest.Services)
                {
                    foreach (var a in svc.Actions)
                    {
                        if (svc.Service.Contains(q, StringComparison.OrdinalIgnoreCase)
                            || a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                            || a.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
                        {
                            all.Add(new JsonObject
                            {
                                ["service"] = svc.Service,
                                ["name"] = a.Name,
                                ["description"] = a.Description,
                            });
                        }
                    }
                }
                var shown = all.Take(Cap).ToArray();
                var arr = new JsonArray(shown.Cast<JsonNode>().ToArray());
                return new JsonObject
                {
                    ["schema_version"] = "1",
                    ["ok"] = true,
                    ["mode"] = "query",
                    ["query"] = q,
                    ["matches"] = arr,
                    ["total_matches"] = all.Count,
                    ["truncated"] = all.Count > Cap,
                    ["upstream_sha"] = manifest.UpstreamSha,
                }.ToJsonString();
            }

            // Default index
            var indexArr = new JsonArray();
            foreach (var svc in manifest.Services.OrderBy(s => s.Service, StringComparer.Ordinal))
            {
                indexArr.Add(new JsonObject
                {
                    ["service"] = svc.Service,
                    ["displayName"] = svc.DisplayName,
                    ["actionCount"] = svc.Actions.Count,
                    ["authTypes"] = new JsonArray(svc.AuthTypes.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
                });
            }
            return new JsonObject
            {
                ["schema_version"] = "1",
                ["ok"] = true,
                ["mode"] = "index",
                ["services"] = indexArr,
                ["total_services"] = manifest.Services.Count,
                ["hint"] = "call connector_list({service:\"<name>\"}) to drill in, or connector_list({query:\"<text>\"}) for fuzzy search",
                ["upstream_sha"] = manifest.UpstreamSha,
            }.ToJsonString();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Envelope(false, service, null, ex.Message, "RUNTIME_HOST_ERROR");
        }
    }

    [McpServerTool(Name = "connector_describe")]
    [Description(
        "Full input/output JSON schema + required OAuth scopes for one action. " +
        "Call before connector_run when arguments are non-trivial.")]
    public string ConnectorDescribe(
        [Description("Service id (from connector_list).")] string service,
        [Description("Action name (from connector_list).")] string name,
        CancellationToken ct = default)
    {
        try
        {
            var manifest = _runtime.ListManifest();
            var svc = manifest.Services.FirstOrDefault(s => string.Equals(s.Service, service, StringComparison.OrdinalIgnoreCase));
            if (svc is null) return Envelope(false, service, name, $"service '{service}' not in catalog", "RUNTIME_NOT_FOUND");
            var action = svc.Actions.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (action is null) return Envelope(false, service, name, $"action '{service}.{name}' not in catalog", "RUNTIME_NOT_FOUND");

            return new JsonObject
            {
                ["schema_version"] = "1",
                ["ok"] = true,
                ["service"] = svc.Service,
                ["name"] = action.Name,
                ["id"] = action.Id,
                ["description"] = action.Description,
                ["requiredScopes"] = new JsonArray(action.RequiredScopes.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
                ["inputSchema"] = action.InputSchema?.DeepClone(),
                ["outputSchema"] = action.OutputSchema?.DeepClone(),
                ["upstream_sha"] = manifest.UpstreamSha,
            }.ToJsonString();
        }
        catch (Exception ex)
        {
            return Envelope(false, service, name, ex.Message, "RUNTIME_HOST_ERROR");
        }
    }

    [McpServerTool(Name = "connector_run")]
    [Description(
        "Execute one provider action. Credentials must be configured " +
        "(env var EVERYWHERE_CONNECTOR_<SERVICE>_PAT or via connector_connect). " +
        "arguments_json is a JSON object matching the action's inputSchema — " +
        "call connector_describe first if unsure. " +
        "connection routes to a named connection (e.g. \"work\" → github:work); " +
        "omit for the default connection.")]
    public async Task<string> ConnectorRun(
        [Description("Service id (from connector_list).")] string service,
        [Description("Action name (from connector_list).")] string name,
        [Description("JSON object of arguments, as a string. Use \"{}\" if no args.")] string arguments_json,
        [Description("Optional named connection (Phase 12). Empty = default connection.")] string? connection = null,
        CancellationToken ct = default)
    {
        JsonObject argsObj;
        if (string.IsNullOrWhiteSpace(arguments_json))
        {
            argsObj = new JsonObject();
        }
        else
        {
            try
            {
                var node = JsonNode.Parse(arguments_json,
                    nodeOptions: null,
                    documentOptions: new JsonDocumentOptions { MaxDepth = 32 });
                if (node is JsonObject parsed) argsObj = parsed;
                else return Envelope(false, service, name, "arguments_json must be a JSON object", "invalid_input");
            }
            catch (JsonException ex)
            {
                return Envelope(false, service, name, $"arguments_json is invalid JSON: {ex.Message}", "invalid_input");
            }
        }

        string? normalizedConnection;
        try { normalizedConnection = NormalizeConnection(connection); }
        catch (ArgumentException ex)
        {
            return Envelope(false, service, name, ex.Message, "invalid_input");
        }
        try
        {
            var result = await _runtime.InvokeAsync(service, name, argsObj, connectionName: normalizedConnection, ct: ct).ConfigureAwait(false);
            return result.ToJsonString();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Envelope(false, service, name, ex.Message, "RUNTIME_HOST_ERROR");
        }
    }

    [McpServerTool(Name = "connector_connect")]
    [Description(
        "Store an api_key credential for a provider (auth_type=api_key). " +
        "Overwrites any existing connection with the same (service, connection) tuple. " +
        "Persists to ~/.everywhere/connector/connections.json (encrypted at rest). " +
        "connection lets you keep multiple accounts per service — e.g. github + \"work\" " +
        "for a work PAT alongside the personal one. Empty = default connection.")]
    public string ConnectorConnect(
        [Description("Service id (e.g. \"github\", \"openai\").")] string service,
        [Description("API key value (personal access token, api key, etc.).")] string api_key,
        [Description("Optional friendly label shown to the user.")] string? display_name = null,
        [Description("Optional connection name. Empty = default connection (Phase 12).")] string? connection = null)
    {
        try
        {
            var normalizedConnection = NormalizeConnection(connection);
            _store.SetApiKey(service, api_key, display_name, connectionName: normalizedConnection);
            var summary = _store.List().FirstOrDefault(c =>
                string.Equals(c.Service, service, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.ConnectionName ?? "", normalizedConnection ?? "", StringComparison.OrdinalIgnoreCase));
            return new JsonObject
            {
                ["schema_version"] = "1",
                ["ok"] = true,
                ["service"] = service,
                ["connection"] = connection,
                ["auth_type"] = "api_key",
                ["display_name"] = summary?.DisplayName,
            }.ToJsonString();
        }
        catch (ArgumentException ex)
        {
            return Envelope(false, service, null, ex.Message, "invalid_input");
        }
        catch (Exception ex)
        {
            return Envelope(false, service, null, ex.Message, "RUNTIME_HOST_ERROR");
        }
    }

    [McpServerTool(Name = "connector_disconnect")]
    [Description("Delete a stored credential for a provider. Idempotent. connection empty = default.")]
    public string ConnectorDisconnect(
        [Description("Service id whose stored credential should be removed.")] string service,
        [Description("Optional connection name (Phase 12). Empty = default connection.")] string? connection = null)
    {
        try
        {
            string? normalizedConnection;
            try { normalizedConnection = NormalizeConnection(connection); }
            catch (ArgumentException ex)
            {
                return Envelope(false, service, null, ex.Message, "invalid_input");
            }
            var removed = _store.DeleteNamed(service, normalizedConnection);
            return new JsonObject
            {
                ["schema_version"] = "1",
                ["ok"] = true,
                ["service"] = service,
                ["connection"] = normalizedConnection,
                ["removed"] = removed,
            }.ToJsonString();
        }
        catch (Exception ex)
        {
            return Envelope(false, service, null, ex.Message, "RUNTIME_HOST_ERROR");
        }
    }

    [McpServerTool(Name = "connector_list_connections")]
    [Description("List provider connections currently configured on this daemon. Values are never returned — only labels + auth types.")]
    public string ConnectorListConnections()
    {
        try
        {
            var conns = _store.List();
            var arr = new JsonArray();
            foreach (var c in conns)
            {
                arr.Add(new JsonObject
                {
                    ["service"] = c.Service,
                    ["connection"] = c.ConnectionName,
                    ["auth_type"] = c.AuthType,
                    ["display_name"] = c.DisplayName,
                    ["account_id"] = c.AccountId,
                });
            }
            return new JsonObject
            {
                ["schema_version"] = "1",
                ["ok"] = true,
                ["connections"] = arr,
                ["total"] = conns.Count,
            }.ToJsonString();
        }
        catch (Exception ex)
        {
            return Envelope(false, null, null, ex.Message, "RUNTIME_HOST_ERROR");
        }
    }

    // Whitespace-only connection names silently produce phantom keys
    // (`service: `) that users can't list/disconnect from the tool
    // description's "Empty = default" contract. Normalize once, use
    // symmetrically across run/connect/disconnect. Rejects colon so a
    // stray `work:prod` doesn't collide with the storage key separator
    // — see JsonCredentialStore.MakeKey.
    private static string? NormalizeConnection(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.Contains(':'))
            throw new ArgumentException("connection name cannot contain ':' — reserved as the storage-key separator");
        return trimmed;
    }
}
