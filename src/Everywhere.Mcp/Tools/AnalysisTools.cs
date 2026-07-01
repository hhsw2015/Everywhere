using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenCli.Analysis;
using Everywhere.Mcp.OpenCli.Observation;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

/// <summary>
/// SPEC §Phase 2 web_* tools. Every tool consumes a <c>session_id</c>
/// via the shared <see cref="CaptureSessionStore"/>; no browser I/O.
/// </summary>
[McpServerToolType]
public sealed class AnalysisTools
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly CaptureSessionStore _store;
    private readonly HttpClient _http;

    public AnalysisTools(CaptureSessionStore store, HttpClient? http = null)
    {
        _store = store;
        _http = http ?? new HttpClient();
    }

    [McpServerTool(Name = "web_verdict_score")]
    [Description("Score every request in a captured session: likely_data / maybe_data / noise / blocked.")]
    public string WebVerdictScore(string session_id)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try
        {
            var session = _store.Get(session_id);
            var arr = new JsonArray();
            foreach (var o in VerdictScorer.Score(session))
            {
                arr.Add(new JsonObject
                {
                    ["request_id"] = o.RequestId,
                    ["verdict"] = o.Verdict,
                    ["real_data_score"] = o.RealDataScore,
                    ["reasons"] = new JsonArray(o.Reasons.Select(r => (JsonNode)r).ToArray()),
                    ["response_shape"] = ToJsonObject(o.ResponseShape),
                });
            }
            return arr.ToJsonString();
        }
        catch (SessionNotFoundException) { return Err("SESSION_NOT_FOUND", session_id); }
        catch (SessionExpiredException ex) { return Err("SESSION_EXPIRED", ex.Message); }
    }

    [McpServerTool(Name = "web_signature_scheme")]
    [Description("Detect the site's auth/signature scheme (jwt | bearer | basic | oauth1 | hmac_sha256 | none).")]
    public string WebSignatureScheme(string session_id)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try
        {
            var v = SignatureScheme.Detect(_store.Get(session_id));
            return new JsonObject
            {
                ["scheme"] = v.Scheme,
                ["evidence"] = new JsonArray(v.Evidence.Select(e => (JsonNode)new JsonObject
                {
                    ["request_id"] = e.RequestId, ["hint"] = e.Hint,
                }).ToArray()),
                ["examples"] = new JsonArray(v.Examples.Take(10).Select(ex => (JsonNode)new JsonObject
                {
                    ["url"] = ex.Url, ["method"] = ex.Method,
                    ["payload_sha256"] = ex.PayloadSha256,
                    ["payload_sample"] = ex.PayloadSample,
                    ["headers"] = ToJsonObject(ex.Headers),
                }).ToArray()),
            }.ToJsonString();
        }
        catch (SessionNotFoundException) { return Err("SESSION_NOT_FOUND", session_id); }
    }

    [McpServerTool(Name = "web_techstack")]
    [Description("Detect frontend framework, build tool, state library from URLs + body markers.")]
    public string WebTechStack(string session_id)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try
        {
            var t = TechStack.Detect(_store.Get(session_id));
            return new JsonObject
            {
                ["framework"] = t.Framework,
                ["framework_version"] = t.FrameworkVersion,
                ["ui_lib"] = t.UiLib,
                ["state_lib"] = t.StateLib,
                ["build_tool"] = t.BuildTool,
                ["hints"] = new JsonArray(t.Hints.Select(h => (JsonNode)h).ToArray()),
            }.ToJsonString();
        }
        catch (SessionNotFoundException) { return Err("SESSION_NOT_FOUND", session_id); }
    }

    [McpServerTool(Name = "web_js_search")]
    [Description("Regex search over indexed JS bodies in a capture. Snippets are redactor-processed.")]
    public string WebJsSearch(string session_id, string pattern, int? top_k = null)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try
        {
            var index = new JsIndex();
            index.AddFromSession(_store.Get(session_id));
            var hits = index.Search(pattern, top_k ?? 20);
            return new JsonArray(hits.Select(h => (JsonNode)new JsonObject
            {
                ["url"] = h.Url, ["line"] = h.Line, ["col"] = h.Col, ["snippet_redacted"] = h.Snippet,
            }).ToArray()).ToJsonString();
        }
        catch (SessionNotFoundException) { return Err("SESSION_NOT_FOUND", session_id); }
        catch (ArgumentException ex) { return Err("ARGUMENT_ERROR", ex.Message); }
    }

    [McpServerTool(Name = "web_crypto_scan")]
    [Description("Scan a captured JS body for crypto/encoding API usage.")]
    public string WebCryptoScan(string session_id, [Description("URL or sha256 body hash to scan.")] string js_url_or_hash)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try
        {
            var session = _store.Get(session_id);
            string? body = null;
            if (session.Network.BodiesByHash.TryGetValue(js_url_or_hash, out var b)) body = b;
            else
            {
                var req = session.Network.Requests.FirstOrDefault(r => r.Url == js_url_or_hash);
                if (req is not null) session.Network.BodiesByHash.TryGetValue(req.ResponseBodySha256, out body);
            }
            if (body is null) return Err("SCRIPT_NOT_IN_CAPTURE", js_url_or_hash);
            var hits = CryptoScan.Scan(body);
            return new JsonArray(hits.Select(h => (JsonNode)new JsonObject
            {
                ["algo"] = h.Algo, ["api"] = h.Api, ["strength"] = h.Strength, ["snippet"] = h.Snippet,
            }).ToArray()).ToJsonString();
        }
        catch (SessionNotFoundException) { return Err("SESSION_NOT_FOUND", session_id); }
    }

    [McpServerTool(Name = "web_sourcemap_list_candidates")]
    [Description("List sourcemap references discovered in the capture (compiled URL + map URL).")]
    public string WebSourcemapListCandidates(string session_id)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try
        {
            var candidates = SourceMapResolver.ListCandidates(_store.Get(session_id));
            return new JsonArray(candidates.Select(c => (JsonNode)new JsonObject
            {
                ["compiled_url"] = c.CompiledUrl, ["map_url"] = c.MapUrl, ["source"] = c.Source,
            }).ToArray()).ToJsonString();
        }
        catch (SessionNotFoundException) { return Err("SESSION_NOT_FOUND", session_id); }
    }

    [McpServerTool(Name = "web_sourcemap_resolve")]
    [Description("Resolve a compiled (url, line, col) back to the original source through the capture's map.")]
    public string WebSourcemapResolve(string session_id, string url, int line, int col)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        try
        {
            var r = SourceMapResolver.Resolve(_store.Get(session_id), url, line, col);
            if (r is null) return Err("SOURCEMAP_NOT_FOUND", url, new JsonObject { ["url"] = url });
            return new JsonObject
            {
                ["original_file"] = r.OriginalFile,
                ["line"] = r.Line, ["col"] = r.Col,
                ["snippet"] = r.Snippet,
                ["is_ignored"] = r.IsIgnored,
            }.ToJsonString();
        }
        catch (SessionNotFoundException) { return Err("SESSION_NOT_FOUND", session_id); }
    }

    [McpServerTool(Name = "web_js_fetch_same_origin")]
    [Description(
        "Fetch a JS URL for offline analysis. SSRF-guarded: HTTPS only, host must match session.origin, " +
        "block RFC1918/loopback/link-local; 1MB response cap; JS MIME check.")]
    public async Task<string> WebJsFetchSameOrigin(string session_id, string url, CancellationToken ct = default)
    {
        if (!SelfExpandGate.Enabled) return Err("SELFEXPAND_DISABLED", "");
        CaptureSession session;
        try { session = _store.Get(session_id); }
        catch (SessionNotFoundException) { return Err("SESSION_NOT_FOUND", session_id); }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return Err("ARGUMENT_ERROR", "invalid url");
        if (uri.Scheme != "https") return Err("SSRF_BLOCKED", "https-only", new JsonObject { ["url"] = url, ["reason"] = "scheme_not_https" });
        // Cross-origin check
        if (!string.IsNullOrEmpty(session.Origin)
            && !uri.Host.Equals(session.Origin, StringComparison.OrdinalIgnoreCase)
            && !uri.Host.EndsWith("." + session.Origin, StringComparison.OrdinalIgnoreCase))
        {
            return Err("CROSS_ORIGIN", $"expected {session.Origin}", new JsonObject
            {
                ["url"] = url, ["expected_origin"] = session.Origin,
            });
        }
        // Private-network guard. Resolve once and pin the IP into the request
        // so the HttpClient's own DNS lookup can't be rebound between guard
        // and fetch (TOCTOU). We use the resolved IP as the connection target
        // and preserve the original hostname for TLS SNI + Host header.
        IPAddress? pinned = null;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.Host, ct);
            foreach (var ip in addresses)
            {
                if (IsPrivateOrLoopback(ip))
                    return Err("SSRF_BLOCKED", "private_network", new JsonObject { ["url"] = url, ["reason"] = "private_ip" });
            }
            pinned = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                     ?? addresses.FirstOrDefault();
            if (pinned is null)
                return Err("SSRF_BLOCKED", "no_addresses", new JsonObject { ["url"] = url });
        }
        catch (Exception ex) { return Err("SSRF_BLOCKED", "dns_failure", new JsonObject { ["url"] = url, ["reason"] = ex.Message }); }

        // Bind the connection to the pinned IP via SocketsHttpHandler.ConnectCallback
        // so the TCP endpoint is fixed but SNI + Host + TLS cert validation still
        // use the original hostname.
        var pinnedIp = pinned;
        var handler = new System.Net.Http.SocketsHttpHandler
        {
            ConnectCallback = async (context, cancel) =>
            {
                var socket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new System.Net.IPEndPoint(pinnedIp, context.DnsEndPoint.Port), cancel);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
            AllowAutoRedirect = false,
        };
        try
        {
            using var pinnedClient = new HttpClient(handler, disposeHandler: true);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await pinnedClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var ct_val = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!(ct_val.Contains("javascript", StringComparison.OrdinalIgnoreCase)
                  || ct_val.Contains("json", StringComparison.OrdinalIgnoreCase)))
                return Err("MIME_MISMATCH", "not javascript", new JsonObject { ["url"] = url, ["content_type"] = ct_val });

            var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[1024 * 1024];
            var total = 0;
            var ms = new MemoryStream();
            int n;
            while ((n = await stream.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                total += n;
                if (total > 1024 * 1024) return Err("RESPONSE_TOO_LARGE", "1MB cap", new JsonObject { ["url"] = url });
                ms.Write(buffer, 0, n);
            }
            var body = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            return new JsonObject
            {
                ["ok"] = true,
                ["url"] = url,
                ["content_type"] = ct_val,
                ["size"] = total,
                ["body_redacted"] = Redactor.Body(body),
            }.ToJsonString();
        }
        catch (Exception ex) { return Err("FETCH_FAILED", ex.Message); }
    }

    private static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 4)
        {
            if (bytes[0] == 10) return true;
            if (bytes[0] == 127) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            if (bytes[0] == 169 && bytes[1] == 254) return true; // link-local
        }
        if (bytes.Length == 16)
        {
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return true; // fe80::/10
            if (bytes[0] == 0xFC || bytes[0] == 0xFD) return true; // fc00::/7
        }
        return false;
    }

    private static JsonObject ToJsonObject(Dictionary<string, string> shape)
    {
        var o = new JsonObject();
        foreach (var kv in shape) o[kv.Key] = kv.Value;
        return o;
    }

    private static string Err(string code, string message, JsonObject? details = null)
    {
        var o = new JsonObject { ["ok"] = false, ["code"] = code, ["message"] = message };
        if (details is not null) o["details"] = details;
        return o.ToJsonString();
    }
}
