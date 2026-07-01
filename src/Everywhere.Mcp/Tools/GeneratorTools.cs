using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenCli.Analysis;
using Everywhere.Mcp.OpenCli.Gates;
using Everywhere.Mcp.OpenCli.Generator;
using Everywhere.Mcp.OpenCli.Memory;
using Everywhere.Mcp.OpenCli.Observation;
using Everywhere.Mcp.OpenDia;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

/// <summary>
/// SPEC §Phase 5 — adapter scaffold / save / verify / list / drift /
/// regenerate / delete + OpenDia smoke check.
/// </summary>
[McpServerToolType]
public sealed class GeneratorTools
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly CaptureSessionStore _captures;
    private readonly MemoryStore _memory;
    private readonly OpenDiaBridge? _bridge;
    private readonly IClock _clock;
    private readonly AdapterLinter _linter = new();

    public GeneratorTools(CaptureSessionStore captures, MemoryStore memory, OpenDiaBridge? bridge = null, IClock? clock = null)
    {
        _captures = captures;
        _memory = memory;
        _bridge = bridge;
        _clock = clock ?? SystemClock.Instance;
    }

    [McpServerTool(Name = "adapter_scaffold")]
    [Description(
        "Render the OpenCLI adapter skeleton + LLM prompt for a captured session. Requires a prior " +
        "strategy_note_write. Errors: STRATEGY_NOTE_MISSING when the note isn't stored.")]
    public string AdapterScaffold(
        string site, string name, string session_id,
        [Description("Optional description passed through to the skeleton comment.")] string? description = null,
        [Description("Optional neighbor hint keyword — improves neighbor score.")] string? neighbor_hint = null)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try
        {
            var note = _memory.ReadStrategyNote(site, name);
            if (note is null) return Err("STRATEGY_NOTE_MISSING", $"sites/{site}/strategy-notes/{name}.md", new JsonObject { ["site"] = site, ["name"] = name });
            if (!note.IsComplete(out var missing))
                return Err("STRATEGY_NOTE_INCOMPLETE", "incomplete", new JsonObject { ["missing_fields"] = new JsonArray(missing.Select(m => (JsonNode)m).ToArray()) });

            var session = _captures.Get(session_id);
            var verdicts = VerdictScorer.Score(session)
                .Where(v => v.Verdict is "likely_data" or "maybe_data")
                .OrderByDescending(v => v.RealDataScore)
                .ToList();
            var techStack = TechStack.Detect(session);
            var scheme = SignatureScheme.Detect(session).Scheme;
            var fieldMapHints = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["signature_scheme"] = scheme,
                ["framework"] = techStack.Framework ?? "unknown",
                ["build_tool"] = techStack.BuildTool ?? "unknown",
            };

            var pool = LocalRegistry.List().Select(x => new NeighborHint(x.Site, x.Name, "", "public", null, false, Array.Empty<string>()));
            var neighborMatches = Neighbor.Search(
                pool,
                (description ?? "") + " " + (neighbor_hint ?? ""),
                note.Strategy,
                domainSuffix: null,
                browser: note.Strategy is "cookie" or "intercept" or "ui",
                columns: Array.Empty<string>());
            var best = neighborMatches.FirstOrDefault();

            var scaffoldReq = new ScaffoldRequest(
                Site: site, Name: name, SessionId: session_id,
                StrategyNotePath: Path.Combine(EverywherePaths.SitesDir(), site, "strategy-notes", name + ".md"),
                StrategyNote: note,
                Neighbor: best,
                NeighborSource: "",
                NeighborPath: best is null ? "(none)" : LocalRegistry.ResolvePath(best.Hint.Site, best.Hint.Name),
                LikelyEndpoints: verdicts.Take(5).ToList(),
                FieldMapHints: fieldMapHints,
                Description: description ?? "");

            var result = Scaffold.Render(scaffoldReq);
            return new JsonObject
            {
                ["skeleton_source"] = result.SkeletonSource,
                ["neighbor_adapter_source"] = result.NeighborSource,
                ["neighbor_adapter_path"] = result.NeighborPath,
                ["neighbor_hint_weak"] = result.NeighborHintWeak,
                ["llm_prompt"] = result.LlmPrompt,
                ["verdict_endpoints"] = new JsonArray(result.VerdictEndpoints.Select(v => (JsonNode)new JsonObject
                {
                    ["request_id"] = v.RequestId,
                    ["real_data_score"] = v.RealDataScore,
                    ["verdict"] = v.Verdict,
                    ["response_shape"] = ToJsonObject(v.ResponseShape),
                }).ToArray()),
                ["strategy_note"] = JsonNode.Parse(JsonSerializer.Serialize(result.StrategyNote, Json)),
                ["field_map_hints"] = ToJsonObject(result.FieldMapHints),
            }.ToJsonString();
        }
        catch (SessionNotFoundException) { return Err("SESSION_NOT_FOUND", session_id); }
        catch (InvalidIdentifierException ex) { return Err("INVALID_IDENTIFIER", ex.Message); }
    }

    [McpServerTool(Name = "adapter_save")]
    [Description(
        "Persist a generated adapter to ~/.everywhere/adapters/<site>/<name>.js after passing G3-G8. " +
        "Fails with the specific gate error otherwise; MutationGuard consults the site's strategy note.")]
    public string AdapterSave(string site, string name,
        [Description("Full JS adapter source produced from adapter_scaffold.")] string source,
        [Description("VerifyFixture JSON — pinned alongside the adapter.")] string verify_fixture,
        [Description("Session id from capture_start; recorded as provenance in .meta.json.")] string? session_id = null)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        VerifyFixture? fixture = null;
        try { fixture = JsonSerializer.Deserialize<VerifyFixture>(verify_fixture, Json); }
        catch (Exception ex) { return Err("ARGUMENT_ERROR", "verify_fixture: " + ex.Message); }
        if (fixture is null) return Err("ARGUMENT_ERROR", "verify_fixture missing");

        StrategyNote? note;
        try { note = _memory.ReadStrategyNote(site, name); }
        catch (InvalidIdentifierException ex) { return Err("INVALID_IDENTIFIER", ex.Message); }
        var linterResult = _linter.Lint(source, note, fixture);
        if (!linterResult.Ok)
        {
            var first = linterResult.Errors[0];
            return Err(first.Code, first.Message, new JsonObject
            {
                ["gate"] = first.Gate, ["line"] = first.Line, ["snippet"] = first.Snippet,
                ["all_errors"] = new JsonArray(linterResult.Errors.Select(e => (JsonNode)new JsonObject
                {
                    ["code"] = e.Code, ["gate"] = e.Gate, ["message"] = e.Message,
                    ["line"] = e.Line, ["snippet"] = e.Snippet,
                }).ToArray()),
            });
        }

        // SCHEMA_INCOMPATIBLE_OVERWRITE — reject overwriting an existing adapter
        // whose stored 4-tuple patterns are incompatible with the new fixture.
        if (LocalRegistry.Exists(site, name))
        {
            var oldFixture = LocalRegistry.LoadVerify(site, name);
            if (oldFixture is not null)
            {
                var missingCols = oldFixture.Patterns.Keys.Where(k => !fixture.Patterns.ContainsKey(k)).ToList();
                if (missingCols.Count > 0)
                    return Err("SCHEMA_INCOMPATIBLE_OVERWRITE", "new fixture drops columns present in previous",
                        new JsonObject { ["site"] = site, ["name"] = name, ["missing_columns"] = new JsonArray(missingCols.Select(c => (JsonNode)c).ToArray()) });
            }
        }

        var oldMeta = LocalRegistry.LoadMeta(site, name);
        var version = (oldMeta?.AdapterVersion ?? 0) + 1;
        var meta = new GeneratorMeta
        {
            GeneratedAt = _clock.NowMs(),
            SessionId = session_id ?? "",
            StrategyNotePath = Path.Combine(EverywherePaths.SitesDir(), site, "strategy-notes", name + ".md"),
            VerifyFixturePath = LocalRegistry.ResolveVerifyPath(site, name),
            Sha256 = LocalRegistry.Sha256Of(source),
            Origin = "local",
            AdapterVersion = version,
        };
        var path = LocalRegistry.Save(site, name, source, fixture, meta);
        return new JsonObject { ["ok"] = true, ["path"] = path, ["adapter_version"] = version }.ToJsonString();
    }

    [McpServerTool(Name = "adapter_verify")]
    [Description("Runs adapter_lint (G3-G9) against a stored local adapter, verify_fixture in memory.")]
    public string AdapterVerify(string site, string name,
        [Description("Optional stored fixture pathname override; defaults to the paired verify.json.")]
        string? fixture_override = null)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try
        {
            var src = LocalRegistry.LoadSource(site, name);
            if (src is null) return Err("ADAPTER_NOT_FOUND", $"local adapter {site}/{name} missing");
            var fixture = LocalRegistry.LoadVerify(site, name);
            if (fixture is null) return Err("VERIFY_FIXTURE_MISSING", $"no verify.json paired with {site}/{name}");
            var note = _memory.ReadStrategyNote(site, name);
            var res = _linter.Lint(src, note, fixture);
            return new JsonObject
            {
                ["ok"] = res.Ok,
                ["errors"] = new JsonArray(res.Errors.Select(e => (JsonNode)new JsonObject
                {
                    ["code"] = e.Code, ["gate"] = e.Gate, ["message"] = e.Message,
                }).ToArray()),
            }.ToJsonString();
        }
        catch (InvalidIdentifierException ex) { return Err("INVALID_IDENTIFIER", ex.Message); }
    }

    [McpServerTool(Name = "adapter_list_local")]
    [Description("List locally-generated adapters under ~/.everywhere/adapters/.")]
    public string AdapterListLocal()
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        var arr = new JsonArray();
        foreach (var (site, name) in LocalRegistry.List())
        {
            var meta = LocalRegistry.LoadMeta(site, name);
            arr.Add(new JsonObject
            {
                ["site"] = site, ["name"] = name,
                ["generated_at"] = meta?.GeneratedAt ?? 0,
                ["adapter_version"] = meta?.AdapterVersion ?? 1,
                ["sha256"] = meta?.Sha256 ?? "",
            });
        }
        return arr.ToJsonString();
    }

    [McpServerTool(Name = "adapter_drift_check")]
    [Description("Compare current adapter output to stored last_success_hash; classifies ok|drift|broken.")]
    public string AdapterDriftCheck(string site, string name, [Description("Current adapter output as string (e.g. JSON.stringify of rows).")] string current_output)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try
        {
            var meta = LocalRegistry.LoadMeta(site, name);
            var fixture = LocalRegistry.LoadVerify(site, name);
            if (meta is null || fixture is null) return Err("ADAPTER_NOT_FOUND", $"{site}/{name}");
            var report = DriftDetector.Compare(current_output, fixture, meta.LastSuccessHash, _clock.NowMs());
            return new JsonObject { ["status"] = report.Status, ["diff"] = report.Diff, ["checked_at"] = report.CheckedAt }.ToJsonString();
        }
        catch (InvalidIdentifierException ex) { return Err("INVALID_IDENTIFIER", ex.Message); }
    }

    [McpServerTool(Name = "adapter_delete_local")]
    [Description("Delete a locally-generated adapter and its meta/verify siblings.")]
    public string AdapterDeleteLocal(string site, string name)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try { LocalRegistry.Delete(site, name); return new JsonObject { ["ok"] = true }.ToJsonString(); }
        catch (InvalidIdentifierException ex) { return Err("INVALID_IDENTIFIER", ex.Message); }
    }

    [McpServerTool(Name = "adapter_regenerate")]
    [Description(
        "Regenerate a local adapter using a fresh capture. Requires session_id or an active capture. " +
        "Reuses the stored strategy note; bumps adapter_version.")]
    public string AdapterRegenerate(string site, string name, string? session_id = null)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        if (string.IsNullOrEmpty(session_id))
            return Err("ADAPTER_REGENERATE_NEEDS_CAPTURE", $"session_id is required for {site}/{name}",
                new JsonObject { ["site"] = site, ["name"] = name });
        try
        {
            var note = _memory.ReadStrategyNote(site, name);
            if (note is null) return Err("STRATEGY_NOTE_MISSING", $"{site}/{name}");
            // The regen produces a fresh scaffold; body-filling is the agent's job.
            return AdapterScaffold(site, name, session_id);
        }
        catch (InvalidIdentifierException ex) { return Err("INVALID_IDENTIFIER", ex.Message); }
    }

    [McpServerTool(Name = "opendia_smoke_check")]
    [Description(
        "Verify the OpenDia extension exposes every browser_* tool this platform depends on. Returns " +
        "{ok, missing?:[]}. When missing, all Phase 1-5 tools should surface OPENDIA_INCOMPATIBLE.")]
    public string OpendiaSmokeCheck()
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        var required = new[]
        {
            "browser_cdp_list_network_requests", "browser_cdp_get_response_body",
            "browser_cdp_list_console_messages", "browser_cdp_evaluate",
            "browser_network_har_start", "browser_cookies_get", "browser_snapshot",
        };
        if (_bridge is null || !_bridge.IsConnected)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["code"] = "OPENDIA_INCOMPATIBLE",
                ["missing"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
                ["reason"] = "extension_not_connected",
            }.ToJsonString();
        }
        // Cross-check every required tool against the bridge's advertised set.
        var advertised = new HashSet<string>(
            _bridge.AvailableTools
                .Select(o => o["name"]?.GetValue<string>() ?? "")
                .Where(n => !string.IsNullOrEmpty(n)),
            StringComparer.Ordinal);
        var missing = required.Where(r => !advertised.Contains(r)).ToList();
        if (missing.Count > 0)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["code"] = "OPENDIA_INCOMPATIBLE",
                ["missing"] = new JsonArray(missing.Select(m => (JsonNode)m).ToArray()),
                ["reason"] = "required_tools_missing",
            }.ToJsonString();
        }
        return new JsonObject { ["ok"] = true, ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()) }.ToJsonString();
    }

    private static JsonObject ToJsonObject(Dictionary<string, string> map)
    {
        var o = new JsonObject();
        foreach (var kv in map) o[kv.Key] = kv.Value;
        return o;
    }

    private static string Err(string code, string message, JsonObject? details = null)
    {
        var o = new JsonObject { ["ok"] = false, ["code"] = code, ["message"] = message };
        if (details is not null) o["details"] = details;
        return o.ToJsonString();
    }
}
