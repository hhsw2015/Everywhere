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

    public ConnectorTools(ConnectorRuntime runtime)
    {
        _runtime = runtime;
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
        "(Phase 1: env var EVERYWHERE_CONNECTOR_<SERVICE>_PAT). " +
        "arguments_json is a JSON object matching the action's inputSchema — " +
        "call connector_describe first if unsure.")]
    public async Task<string> ConnectorRun(
        [Description("Service id (from connector_list).")] string service,
        [Description("Action name (from connector_list).")] string name,
        [Description("JSON object of arguments, as a string. Use \"{}\" if no args.")] string arguments_json,
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

        try
        {
            var result = await _runtime.InvokeAsync(service, name, argsObj, ct).ConfigureAwait(false);
            return result.ToJsonString();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Envelope(false, service, name, ex.Message, "RUNTIME_HOST_ERROR");
        }
    }
}
