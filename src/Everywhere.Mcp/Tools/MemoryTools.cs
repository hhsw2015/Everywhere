using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenCli.Memory;
using Everywhere.Mcp.OpenCli.Observation;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

/// <summary>
/// SPEC §Phase 3 memory tools. All persistent state under
/// <c>~/.everywhere/sites/&lt;domain&gt;/</c>; the caller never supplies
/// a filesystem path. Gated behind <see cref="SelfExpandGate"/>.
/// </summary>
[McpServerToolType]
public sealed class MemoryTools
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly MemoryStore _store;

    public MemoryTools(MemoryStore store) { _store = store; }

    [McpServerTool(Name = "memory_read")]
    [Description("Read a site's memory summary. Returns {cold:true} when nothing recorded yet.")]
    public string MemoryRead([Description("Site domain (e.g. 'news.ycombinator.com').")] string site)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try
        {
            var freshness = _store.Freshness(site);
            if (freshness == "cold")
                return new JsonObject { ["freshness"] = "cold", ["cold"] = true }.ToJsonString();
            return new JsonObject
            {
                ["freshness"] = freshness,
                ["metadata"] = JsonNode.Parse(JsonSerializer.Serialize(_store.ReadMetadata(site), Json)),
            }.ToJsonString();
        }
        catch (InvalidIdentifierException ex) { return Err("INVALID_IDENTIFIER", ex.Message, new JsonObject { ["arg"] = ex.ArgName }); }
    }

    [McpServerTool(Name = "memory_read_endpoint")]
    [Description("Read a single endpoint spec from a site's memory.")]
    public string MemoryReadEndpoint(string site, string name)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try
        {
            var e = _store.ReadEndpoint(site, name);
            if (e is null) return new JsonObject { ["ok"] = true, ["endpoint"] = null }.ToJsonString();
            return JsonSerializer.Serialize(e, Json);
        }
        catch (InvalidIdentifierException ex) { return Err("INVALID_IDENTIFIER", ex.Message); }
        catch (PathTraversalException ex) { return Err("PATH_TRAVERSAL", ex.Message); }
    }

    [McpServerTool(Name = "memory_write_endpoint")]
    [Description("Persist an EndpointSpec. Fails MERGE_CONFLICT on existing key unless force=true.")]
    public string MemoryWriteEndpoint(
        string site, string name,
        [Description("EndpointSpec as JSON string.")] string spec,
        [Description("Overwrite existing key without merge check.")] bool force = false)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try
        {
            EndpointSpec parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<EndpointSpec>(spec, Json)
                    ?? throw new ArgumentException("spec cannot be null.");
            }
            catch (JsonException je)
            {
                return Err("ARGUMENT_ERROR", "invalid JSON: " + je.Message);
            }
            // SPEC §Phase 3 EndpointSpec required fields.
            var missing = new List<string>();
            if (string.IsNullOrEmpty(parsed.Name)) missing.Add("name");
            if (string.IsNullOrEmpty(parsed.Method)) missing.Add("method");
            if (string.IsNullOrEmpty(parsed.UrlTemplate)) missing.Add("url_template");
            if (string.IsNullOrEmpty(parsed.Strategy)) missing.Add("strategy");
            if (missing.Count > 0)
                return Err("ARGUMENT_ERROR", "endpoint spec missing required fields",
                    new JsonObject { ["missing_fields"] = new JsonArray(missing.Select(m => (JsonNode)m).ToArray()) });
            if (parsed.Method is not ("GET" or "POST" or "PUT" or "DELETE" or "PATCH" or "HEAD" or "OPTIONS"))
                return Err("ARGUMENT_ERROR", "endpoint method must be one of GET/POST/PUT/DELETE/PATCH/HEAD/OPTIONS",
                    new JsonObject { ["method"] = parsed.Method });
            _store.WriteEndpoint(site, name, parsed, force);
            return new JsonObject { ["ok"] = true }.ToJsonString();
        }
        catch (MergeConflictException ex) { return Err("MERGE_CONFLICT", ex.Message, new JsonObject { ["path"] = ex.Path, ["existing_hash"] = ex.ExistingHash }); }
        catch (InvalidIdentifierException ex) { return Err("INVALID_IDENTIFIER", ex.Message); }
        catch (PathTraversalException ex) { return Err("PATH_TRAVERSAL", ex.Message); }
        catch (MemoryLockTimeoutException ex) { return Err("MEMORY_LOCK_TIMEOUT", ex.Message, new JsonObject { ["path"] = ex.Path, ["waited_ms"] = ex.WaitedMs }); }
        catch (Exception ex) { return Err("ARGUMENT_ERROR", ex.Message); }
    }

    [McpServerTool(Name = "memory_write_field_map")]
    [Description("Persist a raw-key → FieldMapEntry mapping. Fails MERGE_CONFLICT per-key without force.")]
    public string MemoryWriteFieldMap(
        string site,
        [Description("JSON object mapping raw key to FieldMapEntry.")] string mapping,
        bool force = false)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, FieldMapEntry>>(mapping, Json)
                ?? new Dictionary<string, FieldMapEntry>();
            _store.WriteFieldMap(site, parsed, force);
            return new JsonObject { ["ok"] = true }.ToJsonString();
        }
        catch (MergeConflictException ex) { return Err("MERGE_CONFLICT", ex.Message); }
        catch (InvalidIdentifierException ex) { return Err("INVALID_IDENTIFIER", ex.Message); }
        catch (Exception ex) { return Err("ARGUMENT_ERROR", ex.Message); }
    }

    [McpServerTool(Name = "memory_write_verify_fixture")]
    [Description("Persist a 4-tuple VerifyFixture keyed by cmd.")]
    public string MemoryWriteVerifyFixture(
        string site, string cmd,
        [Description("VerifyFixture as JSON string.")] string fixture,
        bool force = false)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try
        {
            var parsed = JsonSerializer.Deserialize<VerifyFixture>(fixture, Json)
                ?? throw new ArgumentException("fixture cannot be null.");
            _store.WriteVerifyFixture(site, cmd, parsed, force);
            return new JsonObject { ["ok"] = true }.ToJsonString();
        }
        catch (MergeConflictException ex) { return Err("MERGE_CONFLICT", ex.Message); }
        catch (InvalidIdentifierException ex) { return Err("INVALID_IDENTIFIER", ex.Message); }
        catch (Exception ex) { return Err("ARGUMENT_ERROR", ex.Message); }
    }

    [McpServerTool(Name = "memory_append_note")]
    [Description("Append a freeform note to the site's notes.md with ISO timestamp separator.")]
    public string MemoryAppendNote(string site, string text)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try { _store.AppendNote(site, text); return new JsonObject { ["ok"] = true }.ToJsonString(); }
        catch (InvalidIdentifierException ex) { return Err("INVALID_IDENTIFIER", ex.Message); }
    }

    [McpServerTool(Name = "memory_freshness")]
    [Description("Classify site memory freshness: fresh (<30d) | stale (30-90d) | cold (>90d).")]
    public string MemoryFreshness(string site)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try { return new JsonObject { ["freshness"] = _store.Freshness(site) }.ToJsonString(); }
        catch (InvalidIdentifierException ex) { return Err("INVALID_IDENTIFIER", ex.Message); }
    }

    [McpServerTool(Name = "memory_snapshot")]
    [Description(
        "Write a sanitized copy of a live capture session under sites/<site>/fixtures/<cmd>-<ISO>.json. " +
        "Keeps last 5 per cmd; older entries rotate out.")]
    public string MemorySnapshot(string site, string cmd, [Description("Sanitized JSON body from a caller-scrubbed source.")] string content)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try
        {
            var path = _store.WriteSnapshot(site, cmd, Redactor.Body(content));
            return new JsonObject { ["path"] = path }.ToJsonString();
        }
        catch (InvalidIdentifierException ex) { return Err("INVALID_IDENTIFIER", ex.Message); }
    }

    private static string Err(string code, string message, JsonObject? details = null)
    {
        var o = new JsonObject { ["ok"] = false, ["code"] = code, ["message"] = message };
        if (details is not null) o["details"] = details;
        return o.ToJsonString();
    }
}
