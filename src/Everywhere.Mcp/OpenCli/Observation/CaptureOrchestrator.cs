using System.Text.Json;
using System.Text.Json.Nodes;

namespace Everywhere.Mcp.OpenCli.Observation;

/// <summary>
/// SPEC docs/specs/everywhere-self-expanding.md Phase 2.5 — install the
/// capture hook at capture_start, drain + merge into the session at
/// capture_stop. Uses <see cref="IBrowserCallSink"/> so tests can drive
/// this without a live OpenDia bridge.
/// </summary>
public interface IBrowserCallSink
{
    /// <summary>Invoke an OpenDia tool by unprefixed name (e.g. <c>cdp_evaluate</c>).</summary>
    Task<JsonNode?> CallAsync(string tool, JsonObject args, CancellationToken ct);
}

public sealed class CaptureOrchestrator
{
    private readonly IBrowserCallSink _sink;

    public CaptureOrchestrator(IBrowserCallSink sink) { _sink = sink; }

    /// <summary>
    /// Install the capture probe on the tab. Returns the OpenDia script id
    /// so <see cref="StopAsync"/> can remove it. Best-effort: any failure
    /// is logged (no exception surfaced) — capture still works without the
    /// hook, it just won't record signature samples.
    /// </summary>
    /// <summary>
    /// Install the capture probe on the tab. Returns
    /// <c>(hookInstalled, scriptId, reason?)</c>. The initial cdp_evaluate
    /// is critical for two reasons:
    /// 1. It forces OpenDia's `_cdpAttach` for the tab, which enables
    ///    Network / Runtime / Log domain events; without this, the CDP
    ///    buffer is empty when capture_stop pulls it.
    /// 2. It injects the hook script into the already-loaded document
    ///    (add_init_script alone only fires on subsequent navigations).
    /// Best-effort: cdp_evaluate is always attempted so the attach lands,
    /// even when add_init_script itself is unsupported / rejected.
    /// </summary>
    public async Task<(bool Installed, string? ScriptId, string? Reason)> StartAsync(int tabId, CancellationToken ct)
    {
        var script = CaptureHookJs.Render();
        string? scriptId = null;
        string? reason = null;
        try
        {
            var res = await _sink.CallAsync("add_init_script", new JsonObject
            {
                ["tab_id"] = tabId, ["script"] = script,
            }, ct);
            scriptId = res?["id"]?.GetValue<string>();
        }
        catch (Exception ex)
        {
            reason = "add_init_script:" + ex.Message;
        }
        try
        {
            await _sink.CallAsync("cdp_evaluate", new JsonObject
            {
                ["tab_id"] = tabId, ["expression"] = script,
            }, ct);
        }
        catch (Exception ex)
        {
            reason = (reason is null ? "" : reason + " | ") + "cdp_evaluate:" + ex.Message;
        }
        var installed = scriptId is not null || reason is null;
        return (installed, scriptId, reason);
    }

    /// <summary>
    /// Drain <c>window.__ew_capture__</c>, pull the CDP network + console
    /// buffers from OpenDia, merge into the session, remove the init
    /// script. Best-effort; per-source failures don't cascade.
    /// </summary>
    public async Task StopAsync(string sessionId, int tabId, string? scriptId, CaptureSessionStore store, CancellationToken ct)
    {
        var session = store.Get(sessionId);
        await PullNetworkAsync(session, tabId, store, ct);
        await PullConsoleAsync(session, tabId, store, ct);
        try
        {
            var drain = await _sink.CallAsync("cdp_evaluate", new JsonObject
            {
                ["tab_id"] = tabId, ["expression"] = CaptureHookJs.DrainExpression,
            }, ct);
            var payload = ExtractString(drain);
            if (!string.IsNullOrEmpty(payload) && payload != "null")
            {
                try
                {
                    var doc = JsonNode.Parse(payload);
                    var signatures = doc?["signatures"] as JsonArray;
                    if (signatures is not null)
                    {
                        foreach (var s in signatures)
                        {
                            if (s is not JsonObject o) continue;
                            var sample = new CaptureSession.SignatureSample
                            {
                                Ts = (long)(o["ts"]?.GetValue<long>() ?? 0),
                                Url = Redactor.Url(o["url"]?.GetValue<string>() ?? ""),
                                Method = o["method"]?.GetValue<string>() ?? "GET",
                                PayloadSha256 = o["payload_sha256"]?.GetValue<string>() ?? "",
                                PayloadShape = o["payload_shape"]?.GetValue<string>() ?? "",
                                PayloadSample = RedactSample(o["payload_sample"]?.GetValue<string>()),
                                SignatureHeaders = ExtractSigHeaders(o["signature_headers"]),
                            };
                            store.AppendSignature(sessionId, sample);
                        }
                    }
                    // DOM mutations from the hook.
                    if (doc?["mutations"] is JsonArray muts)
                    {
                        foreach (var mNode in muts)
                        {
                            if (mNode is not JsonObject mo) continue;
                            long ts = mo["ts"] is JsonValue mtv && mtv.TryGetValue<long>(out var mtl) ? mtl : 0;
                            var detail = mo.DeepClone() as JsonObject ?? new JsonObject();
                            detail.Remove("ts");
                            store.AppendMutation(sessionId, new CaptureSession.DomMutation { Ts = ts, Detail = detail });
                        }
                    }
                    // User gestures from the hook.
                    if (doc?["gestures"] is JsonArray gests)
                    {
                        foreach (var gNode in gests)
                        {
                            if (gNode is not JsonObject go) continue;
                            long ts = go["ts"] is JsonValue gtv && gtv.TryGetValue<long>(out var gtl) ? gtl : 0;
                            store.AppendGesture(sessionId, new CaptureSession.UserGesture
                            {
                                Ts = ts,
                                Kind = go["kind"]?.GetValue<string>() ?? "",
                                TargetXpath = go["target_xpath"]?.GetValue<string>() ?? "",
                            });
                        }
                    }
                }
                catch (JsonException) { /* drop malformed */ }
            }
        }
        catch { /* swallow — hook is best-effort */ }
        finally
        {
            if (!string.IsNullOrEmpty(scriptId))
            {
                try
                {
                    await _sink.CallAsync("remove_init_script", new JsonObject
                    {
                        ["tab_id"] = tabId, ["id"] = scriptId,
                    }, ct);
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Pull the OpenDia CDP network buffer for the tab, filtered to entries
    /// after session.StartedAt, and append them (redacted) to the session.
    /// For every entry with a non-empty body-size, also fetch the response
    /// body (up to a soft cap of 200 hits) so the verdict scorer has data.
    /// </summary>
    private async Task PullNetworkAsync(CaptureSession session, int tabId, CaptureSessionStore store, CancellationToken ct)
    {
        JsonNode? res;
        try
        {
            res = await _sink.CallAsync("cdp_list_network_requests", new JsonObject
            {
                ["tab_id"] = tabId,
                ["since_ms"] = session.StartedAt,
                ["limit"] = 2000,
            }, ct);
        }
        catch { return; }
        if (res is not JsonObject o || o["requests"] is not JsonArray arr) return;

        var bodyFetched = 0;
        const int maxBodyFetches = 200;
        foreach (var item in arr)
        {
            if (item is not JsonObject r) continue;
            var reqId = r["requestId"]?.GetValue<string>() ?? "";
            var url = r["url"]?.GetValue<string>() ?? "";
            var method = r["method"]?.GetValue<string>() ?? "GET";
            var mime = r["mime"]?.GetValue<string>() ?? "";
            long size = 0;
            if (r["size"] is JsonValue sv)
            {
                if (sv.TryGetValue<long>(out var sl)) size = sl;
                else if (sv.TryGetValue<int>(out var siRaw)) size = siRaw;
                else if (sv.TryGetValue<double>(out var sd)) size = (long)sd;
            }
            long status = 0;
            if (r["status"] is JsonValue stv)
            {
                if (stv.TryGetValue<int>(out var stiRaw)) status = stiRaw;
                else if (stv.TryGetValue<long>(out var stlRaw)) status = stlRaw;
                else if (stv.TryGetValue<double>(out var std)) status = (long)std;
            }
            long ts = 0;
            if (r["ts"] is JsonValue tv)
            {
                if (tv.TryGetValue<long>(out var tlRaw)) ts = tlRaw;
                else if (tv.TryGetValue<int>(out var tiRaw)) ts = tiRaw;
                else if (tv.TryGetValue<double>(out var td)) ts = (long)td;
            }

            var netReq = new CaptureSession.NetworkRequest
            {
                RequestId = reqId,
                Url = Redactor.Url(url),
                Method = method,
                Status = (int)status,
                ResponseSize = size,
                ResponseContentType = mime,
                TimingMs = ts - session.StartedAt,
                // OpenDia doesn't advertise request/response headers or
                // initiator through cdp_list_network_requests today — we
                // record what we have; Phase 2 verdict scorer §Phase 2
                // rules 1-6+8 still classify correctly without them.
            };

            string? bodyContent = null;
            var shouldFetch = size > 0
                              && bodyFetched < maxBodyFetches
                              && !string.IsNullOrEmpty(reqId)
                              && (mime.Contains("json", StringComparison.OrdinalIgnoreCase)
                                  || mime.Contains("text", StringComparison.OrdinalIgnoreCase)
                                  || mime.Contains("javascript", StringComparison.OrdinalIgnoreCase)
                                  || url.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                  || url.EndsWith(".js", StringComparison.OrdinalIgnoreCase));
            if (shouldFetch)
            {
                try
                {
                    var bodyRes = await _sink.CallAsync("cdp_get_response_body", new JsonObject
                    {
                        ["tab_id"] = tabId,
                        ["request_id"] = reqId,
                    }, ct);
                    if (bodyRes is JsonObject bo && bo["body"] is JsonValue bv && bv.TryGetValue<string>(out var bs))
                    {
                        // Bodies capped to 512KB per entry (SPEC §Phase 1).
                        var trimmed = bs.Length > 512 * 1024 ? bs[..(512 * 1024)] : bs;
                        var redacted = Redactor.Body(trimmed);
                        var sha = ComputeSha256(redacted);
                        bodyContent = redacted;
                        // Backfill the sha; NetworkRequest is init-only so
                        // rebuild.
                        netReq = new CaptureSession.NetworkRequest
                        {
                            RequestId = netReq.RequestId, Url = netReq.Url, Method = netReq.Method,
                            Status = netReq.Status, ResponseSize = netReq.ResponseSize,
                            ResponseContentType = netReq.ResponseContentType, TimingMs = netReq.TimingMs,
                            ResponseBodySha256 = sha,
                        };
                        bodyFetched++;
                    }
                }
                catch { /* body pull failed — keep the request entry */ }
            }
            store.AppendRequest(session.SessionId, netReq, bodyContent);
        }
    }

    private async Task PullConsoleAsync(CaptureSession session, int tabId, CaptureSessionStore store, CancellationToken ct)
    {
        JsonNode? res;
        try
        {
            res = await _sink.CallAsync("cdp_list_console_messages", new JsonObject
            {
                ["tab_id"] = tabId,
                ["limit"] = 500,
            }, ct);
        }
        catch { return; }
        if (res is not JsonObject o || o["messages"] is not JsonArray arr) return;
        foreach (var item in arr)
        {
            if (item is not JsonObject m) continue;
            long ts = 0;
            if (m["ts"] is JsonValue tv)
            {
                if (tv.TryGetValue<long>(out var tlRaw)) ts = tlRaw;
                else if (tv.TryGetValue<int>(out var tiRaw)) ts = tiRaw;
                else if (tv.TryGetValue<double>(out var td)) ts = (long)td;
            }
            if (ts < session.StartedAt) continue; // pre-capture noise
            store.AppendConsole(session.SessionId, new CaptureSession.ConsoleMessage
            {
                Ts = ts,
                Level = m["level"]?.GetValue<string>() ?? "log",
                Text = Redactor.Body(m["text"]?.GetValue<string>() ?? ""),
            });
        }
    }

    private static string ComputeSha256(string s)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? RedactSample(string? sample)
    {
        if (string.IsNullOrEmpty(sample)) return sample;
        // Trim to 512 char (client-side already, but re-enforce) and pass
        // through Redactor so JWT / provider tokens don't leak from the
        // hook path.
        var trimmed = sample.Length > 512 ? sample[..512] : sample;
        return Redactor.Body(trimmed);
    }

    private static Dictionary<string, string> ExtractSigHeaders(JsonNode? node)
    {
        var out_ = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node is not JsonObject o) return out_;
        foreach (var kv in o)
        {
            var v = kv.Value is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : kv.Value?.ToString() ?? "";
            // Redact only the value if it looks like a JWT / provider secret; keep the
            // header name and the fact that the header was present.
            out_[kv.Key] = Redactor.Body(v);
        }
        return out_;
    }

    private static string ExtractString(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        // Some bridge implementations wrap { result: "..." } — accept either shape.
        if (node is JsonObject o && o["result"] is JsonValue rv && rv.TryGetValue<string>(out var rs)) return rs;
        return node.ToJsonString();
    }
}
