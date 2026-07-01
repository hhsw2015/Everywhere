using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Everywhere.Mcp.OpenCli.Observation;

namespace Everywhere.Mcp.OpenCli.Analysis;

/// <summary>
/// SPEC §Phase 2 verdict scorer. Rules 1-8 as documented; response_shape
/// (§10.3) is computed alongside — types only, no values.
/// </summary>
public sealed record VerdictOutcome(
    string RequestId,
    string Verdict,           // likely_data | maybe_data | noise | blocked
    int RealDataScore,
    List<string> Reasons,
    Dictionary<string, string> ResponseShape);

public static class VerdictScorer
{
    private static readonly Regex AnalyticsUrl = new(
        @"(google-analytics|gtag|beacon|analytics|track|pixel|sentry|amplitude|" +
        @"mixpanel|segment|hotjar|clarity|newrelic|datadog|insight|telemetry|collect|" +
        @"logrocket|fullstory|error|report|impression)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> EnvelopeKeys = new(StringComparer.OrdinalIgnoreCase)
    { "status", "ok", "success", "error", "message", "code" };

    private static readonly HashSet<string> BusinessKeys = new(StringComparer.OrdinalIgnoreCase)
    { "data", "items", "list", "results", "records", "rows", "edges", "entities", "payload", "response" };

    public static List<VerdictOutcome> Score(CaptureSession session)
    {
        var results = new List<VerdictOutcome>();
        foreach (var req in session.Network.Requests)
            results.Add(ScoreOne(session, req));
        return results;
    }

    public static VerdictOutcome ScoreOne(CaptureSession session, CaptureSession.NetworkRequest req)
    {
        var reasons = new List<string>();
        var contentType = req.ResponseContentType ?? "";
        var isJson = contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                     || req.Url.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        var shape = new Dictionary<string, string>();

        // Rule 1: HTTP >= 400 → blocked
        if (req.Status >= 400)
        {
            var reason = req.Status is 401 or 403 ? "auth_fail" : "http_error";
            reasons.Add(reason);
            return new VerdictOutcome(req.RequestId, "blocked", 0, reasons, shape);
        }

        // Rule 2: non-json → noise
        if (!isJson)
        {
            reasons.Add("not_json");
            return new VerdictOutcome(req.RequestId, "noise", 0, reasons, shape);
        }
        // Rule 3: trivially small
        if (req.ResponseSize < 32)
        {
            reasons.Add("trivial_body");
            return new VerdictOutcome(req.RequestId, "noise", 0, reasons, shape);
        }
        // Rule 4: analytics URL
        if (AnalyticsUrl.IsMatch(req.Url))
        {
            reasons.Add("analytics_url");
            return new VerdictOutcome(req.RequestId, "noise", 0, reasons, shape);
        }

        // Score body if we have it
        var score = 0;
        JsonNode? body = null;
        if (session.Network.BodiesByHash.TryGetValue(req.ResponseBodySha256, out var raw))
        {
            try { body = JsonNode.Parse(raw); }
            catch { /* not JSON — noise */ }
            if (body is null)
            {
                reasons.Add("not_json_body");
                return new VerdictOutcome(req.RequestId, "noise", 0, reasons, shape);
            }
        }

        if (body is JsonObject obj)
        {
            var topLevel = obj.Select(kv => kv.Key).ToList();
            if (topLevel.All(k => EnvelopeKeys.Contains(k)) && topLevel.Count > 0)
            {
                score -= 30;
                reasons.Add("envelope_only");
            }
            foreach (var kv in obj)
            {
                if (BusinessKeys.Contains(kv.Key))
                {
                    if (kv.Value is JsonArray arr && arr.Count > 0 && arr[0] is JsonObject firstObj && firstObj.Count >= 3)
                    {
                        score += 40; reasons.Add("business_shape"); break;
                    }
                    if (kv.Value is JsonObject nested && nested.Count >= 3)
                    {
                        score += 40; reasons.Add("business_shape"); break;
                    }
                }
            }
        }

        // Rule 7: initiator frame url host == session.origin
        if (req.InitiatorStack.Count > 0 && !string.IsNullOrEmpty(session.Origin))
        {
            foreach (var frame in req.InitiatorStack)
            {
                if (Uri.TryCreate(frame.Url, UriKind.Absolute, out var u)
                    && u.Host.EndsWith(session.Origin, StringComparison.OrdinalIgnoreCase))
                {
                    score += 20; reasons.Add("own_bundle"); break;
                }
            }
        }

        // Rule 8: reasonable size
        if (req.ResponseSize is >= 500 and <= 500000)
        {
            score += 10; reasons.Add("reasonable_size");
        }

        // Response shape (§10.3)
        if (body is not null) FlattenShape(body, "", shape, depth: 0);

        var verdict = score >= 40 ? "likely_data" : score >= 15 ? "maybe_data" : "noise";
        return new VerdictOutcome(req.RequestId, verdict, score, reasons, shape);
    }

    private static void FlattenShape(JsonNode? node, string path, Dictionary<string, string> shape, int depth)
    {
        if (node is null || depth > 5 || shape.Count > 100) return;
        switch (node)
        {
            case JsonObject o:
                foreach (var kv in o)
                {
                    if (shape.Count > 100) return;
                    var next = string.IsNullOrEmpty(path) ? kv.Key : $"{path}.{kv.Key}";
                    FlattenShape(kv.Value, next, shape, depth + 1);
                }
                break;
            case JsonArray a:
                if (a.Count > 0) FlattenShape(a[0], path + "[]", shape, depth + 1);
                else shape[path] = "array";
                break;
            case JsonValue v:
                shape[path] = TypeOf(v);
                break;
        }
    }

    private static string TypeOf(JsonValue v)
    {
        var el = v.GetValue<JsonElement>();
        return el.ValueKind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            _ => "any",
        };
    }
}
