using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Everywhere.Mcp.OpenCli.Observation;

/// <summary>
/// SPEC §Phase 1 CaptureSession schema. Field names locked by §10.1 —
/// do not rename. Downstream (Phase 2 verdict, Phase 3 memory, Phase 5
/// scaffold) reads by these exact keys.
/// </summary>
public sealed class CaptureSession
{
    [JsonPropertyName("session_id")] public string SessionId { get; init; } = "";
    [JsonPropertyName("tab_id")] public int TabId { get; init; }
    [JsonPropertyName("origin")] public string Origin { get; init; } = "";
    [JsonPropertyName("started_at")] public long StartedAt { get; init; }
    [JsonPropertyName("stopped_at")] public long? StoppedAt { get; set; }

    [JsonPropertyName("network")] public NetworkSection Network { get; init; } = new();
    [JsonPropertyName("console")] public ConsoleSection Console { get; init; } = new();
    [JsonPropertyName("dom_mutations")] public List<DomMutation> DomMutations { get; init; } = [];
    [JsonPropertyName("user_gestures")] public List<UserGesture> UserGestures { get; init; } = [];

    /// <summary>
    /// SPEC docs/specs/everywhere-self-expanding.md Phase 2.5 (signature
    /// capture hook). One entry per fetch/XHR observed by the injected
    /// hook — payload + header pair, so downstream analysis can recover
    /// (input → signature) examples without live re-execution.
    /// </summary>
    [JsonPropertyName("signatures")] public List<SignatureSample> Signatures { get; init; } = [];

    public sealed class NetworkSection
    {
        [JsonPropertyName("requests")] public List<NetworkRequest> Requests { get; init; } = [];
        [JsonPropertyName("bodies_by_hash")] public Dictionary<string, string> BodiesByHash { get; init; } = new(StringComparer.Ordinal);
    }

    public sealed class NetworkRequest
    {
        [JsonPropertyName("request_id")] public string RequestId { get; init; } = "";
        [JsonPropertyName("url")] public string Url { get; init; } = "";
        [JsonPropertyName("method")] public string Method { get; init; } = "GET";
        [JsonPropertyName("status")] public int Status { get; init; }
        [JsonPropertyName("request_headers")] public Dictionary<string, string> RequestHeaders { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonPropertyName("response_headers")] public Dictionary<string, string> ResponseHeaders { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonPropertyName("response_body_sha256")] public string ResponseBodySha256 { get; init; } = "";
        [JsonPropertyName("response_size")] public long ResponseSize { get; init; }
        [JsonPropertyName("response_content_type")] public string ResponseContentType { get; init; } = "";
        [JsonPropertyName("timing_ms")] public double TimingMs { get; init; }
        [JsonPropertyName("initiator_stack")] public List<InitiatorFrame> InitiatorStack { get; init; } = [];
    }

    public sealed class InitiatorFrame
    {
        [JsonPropertyName("url")] public string Url { get; init; } = "";
        [JsonPropertyName("function")] public string Function { get; init; } = "";
        [JsonPropertyName("line")] public int Line { get; init; }
        [JsonPropertyName("col")] public int Col { get; init; }
    }

    public sealed class ConsoleSection
    {
        [JsonPropertyName("messages")] public List<ConsoleMessage> Messages { get; init; } = [];
    }

    public sealed class ConsoleMessage
    {
        [JsonPropertyName("ts")] public long Ts { get; init; }
        [JsonPropertyName("level")] public string Level { get; init; } = "log";
        [JsonPropertyName("text")] public string Text { get; init; } = "";
        [JsonPropertyName("stack")] public string? Stack { get; init; }
    }

    public sealed class DomMutation
    {
        [JsonPropertyName("ts")] public long Ts { get; init; }
        [JsonPropertyName("detail")] public JsonObject Detail { get; init; } = new();
    }

    public sealed class UserGesture
    {
        [JsonPropertyName("ts")] public long Ts { get; init; }
        [JsonPropertyName("kind")] public string Kind { get; init; } = "";
        [JsonPropertyName("target_xpath")] public string TargetXpath { get; init; } = "";
    }

    /// <summary>
    /// A single (input → header) example observed by the injected hook.
    /// The payload/header pair lets Phase 2 recover the algorithm shape
    /// (e.g. "X-Sign = hex(hmac-sha256(secret, JSON.stringify(payload) + ts))")
    /// from behavior instead of guessing from static JS analysis.
    /// </summary>
    public sealed class SignatureSample
    {
        [JsonPropertyName("ts")] public long Ts { get; init; }
        [JsonPropertyName("url")] public string Url { get; init; } = "";
        [JsonPropertyName("method")] public string Method { get; init; } = "";
        [JsonPropertyName("payload_sha256")] public string PayloadSha256 { get; init; } = "";
        [JsonPropertyName("payload_shape")] public string PayloadShape { get; init; } = "";
        [JsonPropertyName("payload_sample")] public string? PayloadSample { get; init; }
        [JsonPropertyName("signature_headers")] public Dictionary<string, string> SignatureHeaders { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Transforms raw CDP Network.Request.initiator.stack.callFrames[] shape
/// into the canonical Phase 1 InitiatorFrame list. See §10.1.
/// </summary>
public static class InitiatorTransformer
{
    public static List<CaptureSession.InitiatorFrame> FromCdp(JsonNode? initiator)
    {
        var frames = new List<CaptureSession.InitiatorFrame>();
        if (initiator is not JsonObject o) return frames;
        if (o["stack"] is not JsonObject stack) return frames;
        if (stack["callFrames"] is not JsonArray arr) return frames;
        foreach (var item in arr)
        {
            if (item is not JsonObject f) continue;
            frames.Add(new CaptureSession.InitiatorFrame
            {
                Url = TryStr(f["url"]),
                Function = TryStr(f["functionName"]),
                Line = TryInt(f["lineNumber"]),
                Col = TryInt(f["columnNumber"]),
            });
        }
        return frames;
    }

    private static string TryStr(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    private static int TryInt(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
}
