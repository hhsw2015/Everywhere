using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Everywhere.Mcp.OpenCli.Observation;

/// <summary>
/// SPEC §Phase 1 Redactor. Basic-set sanitizer applied at CaptureSession
/// write time. Deliberately not a full PII scanner (§9 non-goal).
/// </summary>
public static class Redactor
{
    private static readonly HashSet<string> SensitiveHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cookie", "set-cookie",
        "authorization", "proxy-authorization",
        "x-csrf-token", "x-api-key",
        "x-auth-token", "x-access-token",
        "x-amz-security-token",
    };

    private static readonly HashSet<string> SensitiveBodyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "session_token", "access_token", "refresh_token", "id_token", "client_secret",
    };

    private static readonly HashSet<string> SensitiveQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "api_key", "access_token", "refresh_token", "code", "secret", "password",
    };

    // JWT-ish literal (§Phase 1 Redactor). Bound length to real-world max
    // (~4096) so a runaway body doesn't get consumed as one giant JWT.
    private static readonly Regex JwtLike = new(
        @"eyJ[A-Za-z0-9+/=._-]{20,4096}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Provider tokens
    private static readonly (Regex Rx, string Label)[] Providers =
    [
        (new Regex(@"gh[psuo]_[A-Za-z0-9]{36}", RegexOptions.Compiled), "GITHUB"),
        (new Regex(@"github_pat_[A-Za-z0-9_]{22,}", RegexOptions.Compiled), "GITHUB"),
        (new Regex(@"sk_(live|test)_[A-Za-z0-9]{24,255}", RegexOptions.Compiled), "STRIPE"),
        (new Regex(@"xox[baprs]-[A-Za-z0-9-]{10,255}", RegexOptions.Compiled), "SLACK"),
        (new Regex(@"AKIA[0-9A-Z]{16}", RegexOptions.Compiled), "AWS"),
    ];

    /// <summary>Redact a header dictionary in-place, returning a new copy.</summary>
    public static Dictionary<string, string> Headers(IEnumerable<KeyValuePair<string, string>>? headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is null) return result;
        foreach (var kv in headers)
        {
            if (SensitiveHeaderNames.Contains(kv.Key))
            {
                result[kv.Key] = $"<REDACTED:{kv.Key}>";
            }
            else
            {
                result[kv.Key] = Body(kv.Value); // still scan for JWT / provider tokens
            }
        }
        return result;
    }

    /// <summary>Redact a URL's sensitive query params. Preserves scheme/path.</summary>
    public static string Url(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        var qIdx = url.IndexOf('?');
        if (qIdx < 0) return SweepProviders(url);
        var prefix = url[..qIdx];
        var query = url[(qIdx + 1)..];
        var fragIdx = query.IndexOf('#');
        var frag = fragIdx >= 0 ? query[fragIdx..] : "";
        if (fragIdx >= 0) query = query[..fragIdx];
        var parts = query.Split('&');
        for (var i = 0; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq < 0) continue;
            var key = parts[i][..eq];
            if (SensitiveQueryKeys.Contains(key))
                parts[i] = key + "=<REDACTED>";
        }
        return SweepProviders(prefix + "?" + string.Join('&', parts) + frag);
    }

    /// <summary>Redact JWT / provider patterns from a free-text body.</summary>
    public static string Body(string body)
    {
        if (string.IsNullOrEmpty(body)) return body;
        body = JwtLike.Replace(body, "<REDACTED:JWT>");
        return SweepProviders(body);
    }

    /// <summary>Redact sensitive keys inside a JSON body while preserving structure.</summary>
    public static JsonNode? JsonBody(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonObject obj)
        {
            var replacement = new JsonObject();
            foreach (var kv in obj)
            {
                if (SensitiveBodyKeys.Contains(kv.Key))
                    replacement[kv.Key] = "<REDACTED>";
                else
                    replacement[kv.Key] = JsonBody(kv.Value?.DeepClone());
            }
            return replacement;
        }
        if (node is JsonArray arr)
        {
            var replacement = new JsonArray();
            foreach (var item in arr) replacement.Add(JsonBody(item?.DeepClone()));
            return replacement;
        }
        if (node is JsonValue v && v.TryGetValue<string>(out var s))
            return Body(s);
        return node.DeepClone();
    }

    private static string SweepProviders(string s)
    {
        foreach (var (rx, label) in Providers)
            s = rx.Replace(s, $"<REDACTED:{label}>");
        return s;
    }
}
