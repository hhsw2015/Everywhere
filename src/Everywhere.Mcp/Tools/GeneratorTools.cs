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
    private readonly Everywhere.Mcp.OpenCli.OpenCliRuntime? _runtime;

    public GeneratorTools(
        CaptureSessionStore captures,
        MemoryStore memory,
        OpenDiaBridge? bridge = null,
        IClock? clock = null,
        Everywhere.Mcp.OpenCli.OpenCliRuntime? runtime = null)
    {
        _captures = captures;
        _memory = memory;
        _bridge = bridge;
        _clock = clock ?? SystemClock.Instance;
        _runtime = runtime;
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
    [Description("Runs G3-G9 lints then invokes the adapter and checks 4-tuple fixture patterns against real output.")]
    public async Task<string> AdapterVerify(string site, string name,
        [Description("Optional stored fixture pathname override; defaults to the paired verify.json.")]
        string? fixture_override = null,
        CancellationToken ct = default)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try
        {
            var src = LocalRegistry.LoadSource(site, name);
            if (src is null) return Err("ADAPTER_NOT_FOUND", $"local adapter {site}/{name} missing");
            var fixture = LocalRegistry.LoadVerify(site, name);
            if (fixture is null) return Err("VERIFY_FIXTURE_MISSING", $"no verify.json paired with {site}/{name}");
            var note = _memory.ReadStrategyNote(site, name);
            var lintRes = _linter.Lint(src, note, fixture);
            var lintErrors = new JsonArray(lintRes.Errors.Select(e => (JsonNode)new JsonObject
            {
                ["code"] = e.Code, ["gate"] = e.Gate, ["message"] = e.Message,
            }).ToArray());
            if (!lintRes.Ok || _runtime is null)
            {
                return new JsonObject
                {
                    ["ok"] = lintRes.Ok,
                    ["errors"] = lintErrors,
                    ["runtime_available"] = _runtime is not null,
                }.ToJsonString();
            }

            // SPEC §Phase 4 G9 — actually run the adapter and check 4-tuple.
            var args = new JsonObject();
            foreach (var (k, v) in fixture.Args) args[k] = v?.DeepClone();
            JsonNode? invokeRes;
            try
            {
                invokeRes = await _runtime.InvokeAsync(site, name, args, new Everywhere.Mcp.OpenCli.Phase1StubPage(), ct);
            }
            catch (Exception ex)
            {
                return Err("VERIFY_INVOKE_FAILED", ex.Message, new JsonObject { ["site"] = site, ["name"] = name });
            }

            var invokeObj = invokeRes as JsonObject;
            if (invokeObj is null || invokeObj["ok"]?.GetValue<bool>() != true)
            {
                return new JsonObject
                {
                    ["ok"] = false,
                    ["stage"] = "invoke",
                    ["errors"] = new JsonArray { "adapter invocation returned not-ok" },
                    ["invoke_result"] = invokeObj?.DeepClone(),
                }.ToJsonString();
            }
            var rows = invokeObj["data"] as JsonArray;
            var mismatches = CheckFixture(fixture, rows);
            var payload = new JsonObject
            {
                ["ok"] = mismatches.Count == 0,
                ["row_count"] = rows?.Count ?? 0,
                ["errors"] = lintErrors,
                ["mismatches"] = new JsonArray(mismatches.Select(m => (JsonNode)m).ToArray()),
            };
            // On success, update meta.last_success_hash + last_success_at for drift check (F8).
            if (mismatches.Count == 0 && rows is not null)
            {
                var meta = LocalRegistry.LoadMeta(site, name);
                if (meta is not null)
                {
                    meta.LastSuccessHash = LocalRegistry.Sha256Of(rows.ToJsonString());
                    meta.LastSuccessAt = _clock.NowMs();
                    LocalRegistry.SaveMetaOnly(site, name, meta);
                }
            }
            return payload.ToJsonString();
        }
        catch (InvalidIdentifierException ex) { return Err("INVALID_IDENTIFIER", ex.Message); }
    }

    private static List<string> CheckFixture(VerifyFixture fixture, JsonArray? rows)
    {
        var m = new List<string>();
        if (rows is null) { m.Add("no rows returned"); return m; }
        var count = rows.Count;
        if (count < fixture.ExpectedRowCountMin) m.Add($"row_count {count} < min {fixture.ExpectedRowCountMin}");
        if (fixture.ExpectedRowCountMax > 0 && count > fixture.ExpectedRowCountMax) m.Add($"row_count {count} > max {fixture.ExpectedRowCountMax}");
        foreach (var (col, pattern) in fixture.Patterns)
        {
            var rx = new System.Text.RegularExpressions.Regex(pattern);
            foreach (var row in rows)
            {
                if (row is not JsonObject ro || ro[col] is null) continue;
                var val = ro[col]!.ToString();
                if (!rx.IsMatch(val)) { m.Add($"column '{col}' pattern mismatch on value: {Truncate(val, 60)}"); break; }
            }
        }
        foreach (var col in fixture.NotEmpty)
        {
            if (rows.All(r => r is JsonObject ro && (ro[col] is null || string.IsNullOrEmpty(ro[col]!.ToString()))))
                m.Add($"column '{col}' notEmpty violated — all rows blank");
        }
        foreach (var (col, forbidden) in fixture.MustNotContain)
        {
            foreach (var row in rows)
            {
                if (row is not JsonObject ro || ro[col] is null) continue;
                var val = ro[col]!.ToString();
                foreach (var f in forbidden)
                {
                    if (!string.IsNullOrEmpty(f) && val.Contains(f))
                    {
                        m.Add($"column '{col}' contains forbidden substring '{f}'"); break;
                    }
                }
            }
        }
        foreach (var col in fixture.MustBeTruthy)
        {
            if (rows.All(r => r is JsonObject ro && (ro[col] is null || IsFalsyValue(ro[col]!))))
                m.Add($"column '{col}' mustBeTruthy violated — every row falsy/missing");
        }
        return m;
    }

    private static bool IsFalsyValue(JsonNode n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return !b;
            if (v.TryGetValue<int>(out var i)) return i == 0;
            if (v.TryGetValue<double>(out var d)) return d == 0.0;
            if (v.TryGetValue<string>(out var s)) return string.IsNullOrEmpty(s);
        }
        return false;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";

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
        "Re-render the scaffold + LLM prompt for an existing local adapter, reusing its strategy note. " +
        "Requires session_id. Returns the same shape as adapter_scaffold — caller must run adapter_save " +
        "with the LLM-filled body to actually persist a new version.")]
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
        // OpenDia advertises tool names WITHOUT the "browser_" prefix; the
        // bridge builder prefixes them for the outer MCP layer. Normalize by
        // stripping "browser_" from the required list before comparing.
        var advertised = new HashSet<string>(
            _bridge.AvailableTools
                .Select(o => o["name"]?.GetValue<string>() ?? "")
                .Where(n => !string.IsNullOrEmpty(n)),
            StringComparer.Ordinal);
        var missing = required
            .Where(r =>
            {
                var stripped = r.StartsWith("browser_", StringComparison.Ordinal) ? r["browser_".Length..] : r;
                return !advertised.Contains(r) && !advertised.Contains(stripped);
            })
            .ToList();
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
