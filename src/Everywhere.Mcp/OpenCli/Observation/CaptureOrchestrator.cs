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
    public async Task<string?> StartAsync(int tabId, CancellationToken ct)
    {
        var script = CaptureHookJs.Render();
        try
        {
            var res = await _sink.CallAsync("add_init_script", new JsonObject
            {
                ["tab_id"] = tabId, ["script"] = script,
            }, ct);
            // Also inject once for the currently loaded document — add_init_script
            // only applies to subsequent navigations; cdp_evaluate the same source
            // to cover the already-loaded page.
            await _sink.CallAsync("cdp_evaluate", new JsonObject
            {
                ["tab_id"] = tabId, ["expression"] = script,
            }, ct);
            return res?["id"]?.GetValue<string>();
        }
        catch { return null; }
    }

    /// <summary>
    /// Drain <c>window.__ew_capture__</c>, merge into the session, remove the init
    /// script. Best-effort; missing / cleared buffer contributes zero signatures.
    /// </summary>
    public async Task StopAsync(string sessionId, int tabId, string? scriptId, CaptureSessionStore store, CancellationToken ct)
    {
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
