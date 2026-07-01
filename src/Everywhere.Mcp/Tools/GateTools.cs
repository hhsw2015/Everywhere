using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenCli.Gates;
using Everywhere.Mcp.OpenCli.Memory;
using Everywhere.Mcp.OpenCli.Observation;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

/// <summary>
/// SPEC §Phase 4 — strategy_note_write / get, adapter_lint, adapter_verify.
/// The scaffold + save gate points (G1/G2 at scaffold, G3-G8 at save,
/// G9 at verify) are hooked from Phase 5 tools; this file exposes the
/// linter and note store to the agent directly.
/// </summary>
[McpServerToolType]
public sealed class GateTools
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly MemoryStore _memory;
    private readonly AdapterLinter _linter = new();

    public GateTools(MemoryStore memory) { _memory = memory; }

    [McpServerTool(Name = "strategy_note_write")]
    [Description(
        "Persist a StrategyNote for a site/name to memory. Validates evidence ≥3 items × ≥20 chars, " +
        "replay ≥50 chars; returns {path} on success or STRATEGY_NOTE_INCOMPLETE.")]
    public string StrategyNoteWrite(string site, string name,
        [Description("JSON string matching the StrategyNote schema.")] string note)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try
        {
            var parsed = JsonSerializer.Deserialize<StrategyNote>(note, Json)
                ?? throw new ArgumentException("note cannot be null.");
            if (!parsed.IsComplete(out var missing))
                return Err("STRATEGY_NOTE_INCOMPLETE", "note is incomplete",
                    new JsonObject { ["missing_fields"] = new JsonArray(missing.Select(m => (JsonNode)m).ToArray()) });
            // SPEC §2.6 / Phase 4 G7 — evidence naming a mutating verb requires mutation:true.
            var mutationVerb = new System.Text.RegularExpressions.Regex(@"\b(POST|PUT|DELETE|PATCH)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!parsed.Mutation && parsed.Evidence.Any(e => e is not null && mutationVerb.IsMatch(e)))
            {
                return Err("MUTATION_UNAPPROVED",
                    "evidence names a mutating verb (POST/PUT/DELETE/PATCH) but mutation:false",
                    new JsonObject { ["site"] = site, ["name"] = name });
            }
            var path = _memory.WriteStrategyNote(site, name, parsed);
            return new JsonObject { ["path"] = path }.ToJsonString();
        }
        catch (InvalidIdentifierException ex) { return Err("INVALID_IDENTIFIER", ex.Message); }
        catch (PathTraversalException ex) { return Err("PATH_TRAVERSAL", ex.Message); }
        catch (Exception ex) { return Err("ARGUMENT_ERROR", ex.Message); }
    }

    [McpServerTool(Name = "strategy_note_get")]
    [Description("Read a StrategyNote from memory or return null.")]
    public string StrategyNoteGet(string site, string name)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try
        {
            var n = _memory.ReadStrategyNote(site, name);
            if (n is null) return new JsonObject { ["ok"] = true, ["note"] = null }.ToJsonString();
            return JsonSerializer.Serialize(n, Json);
        }
        catch (InvalidIdentifierException ex) { return Err("INVALID_IDENTIFIER", ex.Message); }
    }

    [McpServerTool(Name = "adapter_lint")]
    [Description("Run G3-G8 lints over adapter source. Returns {errors:[], warnings:[]}.")]
    public string AdapterLint(
        [Description("Adapter JS source.")] string source,
        [Description("Optional site — enables G7 mutation guard by loading the note.")] string? site = null,
        [Description("Optional name — pairs with site.")] string? name = null,
        [Description("Optional VerifyFixture JSON — enables G9.")] string? fixture = null)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        StrategyNote? note = null;
        VerifyFixture? verifyFixture = null;
        if (site is not null && name is not null)
        {
            try { note = _memory.ReadStrategyNote(site, name); }
            catch (InvalidIdentifierException ex) { return Err("INVALID_IDENTIFIER", ex.Message); }
        }
        if (!string.IsNullOrEmpty(fixture))
        {
            try { verifyFixture = JsonSerializer.Deserialize<VerifyFixture>(fixture, Json); }
            catch (Exception ex) { return Err("ARGUMENT_ERROR", "fixture: " + ex.Message); }
        }
        var result = _linter.Lint(source, note, verifyFixture);
        return new JsonObject
        {
            ["ok"] = result.Ok,
            ["errors"] = SerializeFindings(result.Errors),
            ["warnings"] = SerializeFindings(result.Warnings),
        }.ToJsonString();
    }

    private static JsonArray SerializeFindings(IEnumerable<GateFinding> f)
    {
        var arr = new JsonArray();
        foreach (var x in f)
        {
            var o = new JsonObject
            {
                ["gate"] = x.Gate,
                ["code"] = x.Code,
                ["message"] = x.Message,
            };
            if (x.Line.HasValue) o["line"] = x.Line.Value;
            if (x.Snippet is not null) o["snippet"] = x.Snippet;
            arr.Add(o);
        }
        return arr;
    }

    private static string Err(string code, string message, JsonObject? details = null)
    {
        var o = new JsonObject { ["ok"] = false, ["code"] = code, ["message"] = message };
        if (details is not null) o["details"] = details;
        return o.ToJsonString();
    }
}
