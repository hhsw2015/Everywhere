using System.Text.Json.Nodes;
using Microsoft.ClearScript;

namespace Everywhere.Mcp.OpenCli;

/// <summary>
/// SPEC §4.4 — the subset of <c>cli({...})</c> registration metadata
/// that the host stores. <c>func</c> is kept as an opaque
/// <see cref="ScriptObject"/> handle for re-entry into the V8 isolate;
/// <c>pipeline</c> is captured as a serialised <see cref="JsonNode"/>
/// (the pipeline runner is forbidden by SPEC §2.4 #1, so we never
/// re-execute it — keep it for diagnostics / surface only).
/// </summary>
public sealed record AdapterDef(
    string Site,
    string Name,
    string Description,
    string Strategy,
    bool Browser,
    string? Access,
    string? Domain,
    IReadOnlyList<string>? Aliases,
    JsonArray? Args,
    JsonArray? Columns,
    ScriptObject? Func,
    JsonNode? Pipeline)
{
    public string FullName => $"{Site}/{Name}";

    /// <summary>SPEC §4.2 — describe envelope.</summary>
    public JsonObject ToDescribeJson()
    {
        var o = new JsonObject
        {
            ["schema_version"] = "1",
            ["site"] = Site,
            ["name"] = Name,
            ["description"] = Description,
            ["strategy"] = Strategy,
            ["browser"] = Browser,
            ["args"] = Args?.DeepClone() ?? new JsonArray(),
            ["columns"] = Columns?.DeepClone() ?? new JsonArray(),
        };
        if (Access is not null) o["access"] = Access;
        if (Domain is not null) o["domain"] = Domain;
        if (Aliases is { Count: > 0 })
        {
            var clean = Aliases.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => (JsonNode?)a).ToArray();
            if (clean.Length > 0) o["aliases"] = new JsonArray(clean);
        }
        return o;
    }

    /// <summary>SPEC §4.1 — one element of the <c>commands</c> array.</summary>
    public JsonObject ToListEntry()
    {
        var o = new JsonObject
        {
            ["site"] = Site,
            ["name"] = Name,
            ["description"] = Description,
            ["strategy"] = Strategy,
            ["browser"] = Browser,
            ["args"] = Args?.DeepClone() ?? new JsonArray(),
        };
        if (Aliases is { Count: > 0 })
        {
            var clean = Aliases.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => (JsonNode?)a).ToArray();
            if (clean.Length > 0) o["aliases"] = new JsonArray(clean);
        }
        return o;
    }
}
