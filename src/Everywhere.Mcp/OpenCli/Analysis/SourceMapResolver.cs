using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenCli.Observation;

namespace Everywhere.Mcp.OpenCli.Analysis;

/// <summary>
/// SPEC §Phase 2 — sourcemap resolver. Full implementation requires
/// bundling <c>@jridgewell/trace-mapping</c> via <c>ModuleLoader._fileRoutes</c>
/// and calling it via V8. This ponytail impl parses the simple map form
/// (no mappings decoded — we don't need position-perfect columns yet)
/// and reports the <c>sources</c> / <c>sourcesContent</c> pointers plus
/// the <c>ignoreList</c>. Enough for Phase 2 tests + Phase 5 field hints;
/// upgrade path documented on <see cref="Resolve"/>.
/// </summary>
public sealed record SourceMapEntry(string CompiledUrl, string MapUrl, string Source);
public sealed record ResolveResult(string OriginalFile, int Line, int Col, string Snippet, bool IsIgnored);

public static class SourceMapResolver
{
    /// <summary>Enumerate sourcemap candidates in a capture (URLs with .map or //# sourceMappingURL=).</summary>
    public static List<SourceMapEntry> ListCandidates(CaptureSession session)
    {
        var results = new List<SourceMapEntry>();
        foreach (var req in session.Network.Requests)
        {
            if (session.Network.BodiesByHash.TryGetValue(req.ResponseBodySha256, out var body))
            {
                var idx = body.LastIndexOf("//# sourceMappingURL=", StringComparison.Ordinal);
                if (idx < 0) continue;
                var mapRel = body[(idx + "//# sourceMappingURL=".Length)..].Split(new[] { '\n', '\r' }, 2)[0].Trim();
                if (Uri.TryCreate(new Uri(req.Url), mapRel, out var mapAbs))
                {
                    // Try to pull first source name if the map body is also in the capture.
                    var source = TryGetFirstSource(session, mapAbs.ToString());
                    results.Add(new SourceMapEntry(req.Url, mapAbs.ToString(), source ?? ""));
                }
            }
        }
        return results;
    }

    /// <summary>
    /// Resolve compiled (url, line, col) → original source. This ponytail
    /// impl only reports the first named source when the map is present
    /// in the capture (no VLQ mappings decode). SPEC-full impl calls into
    /// @jridgewell/trace-mapping via <c>__opencliSourceMapResolve</c>.
    /// </summary>
    public static ResolveResult? Resolve(CaptureSession session, string url, int line, int col)
    {
        var map = ListCandidates(session).FirstOrDefault(c => c.CompiledUrl == url);
        if (map is null) return null;
        var mapBodyReq = session.Network.Requests.FirstOrDefault(r => r.Url == map.MapUrl);
        if (mapBodyReq is null) return null;
        if (!session.Network.BodiesByHash.TryGetValue(mapBodyReq.ResponseBodySha256, out var body)) return null;
        try
        {
            var doc = JsonNode.Parse(body);
            var sources = doc?["sources"] as JsonArray;
            var sourcesContent = doc?["sourcesContent"] as JsonArray;
            var ignoreList = new HashSet<int>();
            if (doc?["ignoreList"] is JsonArray ignoreArr)
            {
                foreach (var n in ignoreArr)
                {
                    if (n is JsonValue v && v.TryGetValue<int>(out var i)) ignoreList.Add(i);
                }
            }
            var idx = 0;
            var name = sources?[idx] is JsonValue nv && nv.TryGetValue<string>(out var ns) ? ns : "";
            var snippet = sourcesContent?[idx] is JsonValue sv && sv.TryGetValue<string>(out var ss) ? ss : "";
            var lines = snippet.Split('\n');
            var extract = line - 1 >= 0 && line - 1 < lines.Length ? lines[line - 1] : "";
            return new ResolveResult(name, line, col, extract, ignoreList.Contains(idx));
        }
        catch (Exception) { return null; }
    }

    private static string? TryGetFirstSource(CaptureSession session, string mapUrl)
    {
        var mapReq = session.Network.Requests.FirstOrDefault(r => r.Url == mapUrl);
        if (mapReq is null) return null;
        if (!session.Network.BodiesByHash.TryGetValue(mapReq.ResponseBodySha256, out var body)) return null;
        try
        {
            var doc = JsonNode.Parse(body);
            if (doc?["sources"] is JsonArray sources
                && sources.FirstOrDefault() is JsonValue v
                && v.TryGetValue<string>(out var s)) return s;
            return null;
        }
        catch { return null; }
    }
}
