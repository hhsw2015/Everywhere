using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenCli.Observation;
using Everywhere.Mcp.OpenDia;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

/// <summary>
/// SPEC §Phase 1 — MCP tools binding existing <c>browser_*</c> observation
/// into a <see cref="CaptureSession"/>. Hidden unless
/// <c>EVERYWHERE_MCP_SELFEXPAND=1</c> (or a Phase 6 session activation).
///
/// All disk-writing tools derive their own path (§2.3) — the caller may
/// never pass a filesystem path.
/// </summary>
[McpServerToolType]
public sealed class CaptureTools
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly CaptureSessionStore _store;
    private readonly OpenDiaBridge? _bridge;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, HookLease> _hooks = new(StringComparer.Ordinal);

    private sealed record HookLease(int TabId, string? ScriptId);

    public CaptureTools(CaptureSessionStore store, OpenDiaBridge? bridge = null)
    {
        _store = store;
        _bridge = bridge;
    }

    // -----------------------------------------------------------------
    // capture_start / capture_stop / capture_current / capture_export
    // -----------------------------------------------------------------

    [McpServerTool(Name = "capture_start")]
    [Description(
        "Start an Everywhere capture session bound to a browser tab. Installs " +
        "the Phase 2.5 signature-capture hook via add_init_script. Returns " +
        "{session_id}. Fails CAPTURE_LIMIT_EXCEEDED at 10 concurrent sessions.")]
    public async Task<string> CaptureStart(
        [Description("Chrome tab id. Optional; active tab is auto-detected via get_url when omitted.")]
        int? tab_id = null,
        [Description("Top-frame origin. Optional; auto-detected from the tab URL when omitted.")]
        string? origin = null,
        CancellationToken ct = default)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");

        // Auto-detect tab_id + origin when either is missing and bridge connected.
        var effectiveTabId = tab_id ?? 0;
        var effectiveOrigin = origin ?? "";
        if (_bridge is not null && _bridge.IsConnected && (effectiveTabId == 0 || string.IsNullOrEmpty(effectiveOrigin)))
        {
            try
            {
                var urlRes = await _bridge.CallToolAsync("get_url", new JsonObject(), ct: ct);
                if (urlRes is JsonObject uo)
                {
                    if (effectiveTabId == 0 && uo["tab_id"] is JsonValue tv)
                    {
                        if (tv.TryGetValue<int>(out var ti)) effectiveTabId = ti;
                        else if (tv.TryGetValue<long>(out var tl)) effectiveTabId = (int)tl;
                    }
                    if (string.IsNullOrEmpty(effectiveOrigin) && uo["url"] is JsonValue urlV && urlV.TryGetValue<string>(out var urlStr)
                        && Uri.TryCreate(urlStr, UriKind.Absolute, out var parsed))
                    {
                        effectiveOrigin = parsed.Host;
                    }
                }
            }
            catch { /* best-effort */ }
        }

        CaptureSession session;
        try
        {
            session = _store.Start(effectiveTabId, effectiveOrigin);
        }
        catch (CaptureLimitException ex)
        {
            return Err("CAPTURE_LIMIT_EXCEEDED", ex.Message, new JsonObject { ["max"] = ex.Max, ["current"] = ex.Current });
        }

        // Install signature hook + force CDP attach via cdp_evaluate.
        bool hookInstalled = false;
        string? hookReason = null;
        if (_bridge is not null && _bridge.IsConnected && effectiveTabId > 0)
        {
            // F17: check for existing lease on same tab — skip duplicate install.
            var already = _hooks.Values.Any(l => l.TabId == effectiveTabId);
            if (!already)
            {
                var orchestrator = new CaptureOrchestrator(new OpenDiaBrowserCallSink(_bridge));
                var (installed, scriptId, reason) = await orchestrator.StartAsync(effectiveTabId, ct);
                hookInstalled = installed;
                hookReason = reason;
                _hooks[session.SessionId] = new HookLease(effectiveTabId, scriptId);
            }
            else
            {
                hookInstalled = true;
                hookReason = "shared_with_existing_lease";
                _hooks[session.SessionId] = new HookLease(effectiveTabId, null);
            }
        }
        else
        {
            hookReason = _bridge is null || !_bridge.IsConnected ? "opendia_not_connected" : "no_tab_id";
        }

        var payload = new JsonObject
        {
            ["session_id"] = session.SessionId,
            ["tab_id"] = effectiveTabId,
            ["origin"] = effectiveOrigin,
            ["hook_installed"] = hookInstalled,
        };
        if (hookReason is not null) payload["hook_reason"] = hookReason;
        return payload.ToJsonString();
    }

    [McpServerTool(Name = "capture_stop")]
    [Description("Finalize a capture session, drain the signature hook, return sanitized CaptureSession JSON.")]
    public async Task<string> CaptureStop(
        [Description("Session id from capture_start.")] string session_id,
        CancellationToken ct = default)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try
        {
            // F28: peek lease; do NOT remove yet — Store.Stop might throw and
            // we want the drain path to run first (which needs the lease).
            _hooks.TryGetValue(session_id, out var lease);
            var currentSession = _store.Get(session_id);
            var tabId = lease?.TabId ?? currentSession.TabId;
            var scriptId = lease?.ScriptId;
            if (_bridge is not null && _bridge.IsConnected && tabId > 0)
            {
                var orchestrator = new CaptureOrchestrator(new OpenDiaBrowserCallSink(_bridge));
                await orchestrator.StopAsync(session_id, tabId, scriptId, _store, ct);
            }
            _hooks.TryRemove(session_id, out _);
            var s = _store.Stop(session_id);
            return JsonSerializer.Serialize(s, Json);
        }
        catch (SessionNotFoundException) { return Err("SESSION_NOT_FOUND", $"session_id={session_id}", new JsonObject { ["session_id"] = session_id }); }
        catch (SessionExpiredException ex) { return Err("SESSION_EXPIRED", ex.Message, new JsonObject { ["session_id"] = ex.SessionId, ["reason"] = ex.Reason }); }
    }

    [McpServerTool(Name = "capture_current")]
    [Description("Live snapshot of a running capture without stopping it.")]
    public string CaptureCurrent(
        [Description("Session id from capture_start.")] string session_id)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try
        {
            var s = _store.Get(session_id);
            return JsonSerializer.Serialize(s, Json);
        }
        catch (SessionNotFoundException) { return Err("SESSION_NOT_FOUND", $"session_id={session_id}"); }
        catch (SessionExpiredException ex) { return Err("SESSION_EXPIRED", ex.Message); }
    }

    [McpServerTool(Name = "capture_export")]
    [Description(
        "Write the session's sanitized JSON to ~/.everywhere/captures/<session_id>.json. " +
        "Returns {path}. Callers do NOT provide the path (spec §2.3).")]
    public string CaptureExport(
        [Description("Session id from capture_start.")] string session_id)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try
        {
            Identifier.Require("session_id", GuardId(session_id));
            var s = _store.Get(session_id);
            var dir = EverywherePaths.CapturesDir();
            var path = Path.Combine(dir, session_id + ".json");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
            File.Move(tmp, path, overwrite: true);
            var payload = new JsonObject
            {
                ["path"] = path,
                ["request_count"] = s.Network.Requests.Count,
                ["signature_count"] = s.Signatures.Count,
                ["console_count"] = s.Console.Messages.Count,
            };
            if (s.Network.Requests.Count == 0)
                payload["warnings"] = new JsonArray { "empty_capture" };
            return payload.ToJsonString();
        }
        catch (InvalidIdentifierException ex) { return Err("INVALID_IDENTIFIER", ex.Message, new JsonObject { ["arg"] = ex.ArgName, ["pattern"] = Identifier.PatternSource }); }
        catch (SessionNotFoundException) { return Err("SESSION_NOT_FOUND", $"session_id={session_id}"); }
        catch (SessionExpiredException ex) { return Err("SESSION_EXPIRED", ex.Message); }
    }

    // -----------------------------------------------------------------
    // browser_captcha_present
    // -----------------------------------------------------------------

    [McpServerTool(Name = "browser_captcha_present")]
    [Description(
        "Detect if the current tab shows a CAPTCHA challenge. Runs the reCAPTCHA v2/v3, " +
        "Cloudflare Turnstile, and hCaptcha detectors over a browser_snapshot HTML fragment.")]
    public async Task<string> BrowserCaptchaPresent(
        [Description("Optional tab id. Uses the active tab when omitted.")]
        int? tab_id = null,
        CancellationToken ct = default)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        if (_bridge is null || !_bridge.IsConnected)
            return Err("OPENDIA_NOT_CONNECTED", "browser extension not connected — cannot detect captcha.");

        var args = new JsonObject();
        if (tab_id.HasValue) args["tab_id"] = tab_id.Value;
        JsonNode? htmlNode;
        JsonNode? cookieNode;
        try
        {
            htmlNode = await _bridge.CallToolAsync("snapshot", args, ct: ct).ConfigureAwait(false);
            cookieNode = await _bridge.CallToolAsync("cookies_get", args, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) { return Err("OPENDIA_CALL_FAILED", ex.Message); }

        var html = ExtractHtmlText(htmlNode);
        var cookieNames = ExtractCookieNames(cookieNode);
        var res = CaptchaDetector.Detect(html, cookieNames);
        return new JsonObject
        {
            ["present"] = res.Present,
            ["kind"] = res.Kind,
            ["confidence"] = res.Confidence,
        }.ToJsonString();
    }

    // -----------------------------------------------------------------
    // page_extract_by_rule / page_save_extraction_rule
    // -----------------------------------------------------------------

    [McpServerTool(Name = "page_extract_by_rule")]
    [Description(
        "Apply the extraction rulebook to the current tab's URL. If no rule matches, " +
        "falls back to browser_get_text (spec §Phase 1).")]
    public async Task<string> PageExtractByRule(
        [Description("Optional URL to match against the rulebook. Defaults to the active tab's URL.")]
        string? url = null,
        CancellationToken ct = default)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        if (_bridge is null || !_bridge.IsConnected)
            return Err("OPENDIA_NOT_CONNECTED", "browser extension not connected.");

        try
        {
            var currentUrl = url;
            if (string.IsNullOrEmpty(currentUrl))
            {
                var u = await _bridge.CallToolAsync("get_url", new JsonObject(), ct: ct).ConfigureAwait(false);
                currentUrl = u?["url"]?.GetValue<string>() ?? u?.GetValue<string>() ?? "";
            }
            var rule = new ExtractionRules().Match(currentUrl ?? "");
            if (rule is null)
            {
                var text = await _bridge.CallToolAsync("get_text", new JsonObject(), ct: ct).ConfigureAwait(false);
                return new JsonObject { ["matched"] = false, ["text"] = text?.ToJsonString() ?? "" }.ToJsonString();
            }
            var callArgs = new JsonObject
            {
                ["selector"] = rule.Selector,
                ["kind"] = rule.Kind,
            };
            var extracted = await _bridge.CallToolAsync("get_text", callArgs, ct: ct).ConfigureAwait(false);
            return new JsonObject
            {
                ["matched"] = true,
                ["rule"] = JsonNode.Parse(JsonSerializer.Serialize(rule, Json)),
                ["text"] = extracted is null ? "" : extracted.ToJsonString(),
            }.ToJsonString();
        }
        catch (Exception ex) { return Err("EXTRACT_FAILED", ex.Message); }
    }

    [McpServerTool(Name = "page_save_extraction_rule")]
    [Description(
        "Persist a URL-pattern → CSS/XPath selector rule to ~/.everywhere/extraction-rules.json. " +
        "First match wins at read time; higher priority sorts first.")]
    public string PageSaveExtractionRule(
        [Description("Regex applied to the page URL (case-insensitive).")] string url_pattern,
        [Description("Selector kind: 'css' or 'xpath'.")] string kind,
        [Description("Selector body.")] string selector,
        [Description("Optional priority — higher applies first. Default 0.")] int? priority = null)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        if (string.IsNullOrWhiteSpace(url_pattern) || string.IsNullOrWhiteSpace(selector))
            return Err("ARGUMENT_ERROR", "url_pattern and selector are required.");
        if (kind is not "css" and not "xpath")
            return Err("ARGUMENT_ERROR", "kind must be 'css' or 'xpath'.");
        var rules = new ExtractionRules();
        rules.Upsert(new ExtractionRules.Rule
        {
            UrlPattern = url_pattern,
            Kind = kind,
            Selector = selector,
            Priority = priority ?? 0,
        });
        return new JsonObject { ["ok"] = true }.ToJsonString();
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    private static string ExtractHtmlText(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return node.ToJsonString();
    }

    private static IEnumerable<string> ExtractCookieNames(JsonNode? node)
    {
        if (node is JsonArray arr)
        {
            foreach (var c in arr)
            {
                if (c is JsonObject o && o["name"] is JsonValue nv && nv.TryGetValue<string>(out var name))
                    yield return name;
            }
        }
    }

    private static string GuardId(string? id)
    {
        // Accept UUIDs (allow uppercase hex / hyphens); Identifier regex is too strict for uuid.
        // But guard against slashes/traversal explicitly.
        if (string.IsNullOrWhiteSpace(id)) return "";
        if (id.Contains('/') || id.Contains("..")) return "";
        return id.ToLowerInvariant();
    }

    private static string Err(string code, string message, JsonObject? details = null)
    {
        var payload = new JsonObject
        {
            ["ok"] = false,
            ["code"] = code,
            ["message"] = message,
        };
        if (details is not null) payload["details"] = details;
        return payload.ToJsonString();
    }
}
