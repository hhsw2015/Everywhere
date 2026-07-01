using System.Text.RegularExpressions;
using Everywhere.Mcp.OpenCli.Observation;

namespace Everywhere.Mcp.OpenCli.Analysis;

/// <summary>
/// SPEC §Phase 2 signature scheme detection. Port of jshookmcp
/// PatternDetectorAuthPatterns heuristics — looks for common auth /
/// signature schemes in captured request headers and response bodies.
/// </summary>
public sealed record SignatureEvidence(string RequestId, string Hint);

/// <summary>
/// Phase 2.5 — an example pair pulled from the injected hook. Downstream
/// analysis can compare <c>payload_sha256</c> across requests with the
/// same signature header shape to recover the algorithm (e.g. "hmac over
/// body || ts").
/// </summary>
public sealed record SignatureExample(string Url, string Method, string PayloadSha256, string? PayloadSample, Dictionary<string, string> Headers);

public sealed record SignatureVerdict(string Scheme, List<SignatureEvidence> Evidence, List<SignatureExample> Examples);

public static class SignatureScheme
{
    private static readonly Regex BearerJwt = new(@"^Bearer\s+eyJ", RegexOptions.IgnoreCase);
    private static readonly Regex Bearer = new(@"^Bearer\s+", RegexOptions.IgnoreCase);
    private static readonly Regex Hmac = new(@"(hmac|signature|hs256|hs512|x-signature)", RegexOptions.IgnoreCase);
    private static readonly Regex Basic = new(@"^Basic\s+", RegexOptions.IgnoreCase);
    private static readonly Regex OAuth1 = new(@"OAuth\s+oauth_consumer_key", RegexOptions.IgnoreCase);

    public static SignatureVerdict Detect(CaptureSession session)
    {
        var evidence = new List<SignatureEvidence>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        void Bump(string scheme, string requestId, string hint)
        {
            counts[scheme] = counts.GetValueOrDefault(scheme) + 1;
            evidence.Add(new SignatureEvidence(requestId, hint));
        }

        foreach (var req in session.Network.Requests)
        {
            foreach (var (name, value) in req.RequestHeaders)
            {
                if (string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    if (BearerJwt.IsMatch(value)) Bump("jwt", req.RequestId, "Bearer eyJ...");
                    else if (Bearer.IsMatch(value)) Bump("bearer", req.RequestId, "Bearer <opaque>");
                    else if (Basic.IsMatch(value)) Bump("basic", req.RequestId, "Basic <base64>");
                    else if (OAuth1.IsMatch(value)) Bump("oauth1", req.RequestId, "OAuth oauth_consumer_key=...");
                }
                if (Hmac.IsMatch(name) || Hmac.IsMatch(value))
                    Bump("hmac_sha256", req.RequestId, name + ": <redacted>");
            }
        }

        // Phase 2.5 — merge in hook-observed examples. A hook sample counts as
        // stronger evidence than a header-only signal, and can promote the
        // scheme to hmac_sha256 when a signature header carries a hex-ish
        // fixed-length blob.
        var examples = new List<SignatureExample>();
        foreach (var sig in session.Signatures)
        {
            examples.Add(new SignatureExample(sig.Url, sig.Method, sig.PayloadSha256, sig.PayloadSample, sig.SignatureHeaders));
            foreach (var (name, value) in sig.SignatureHeaders)
            {
                if (BearerJwt.IsMatch(value)) Bump("jwt", sig.Url, $"hook: {name}");
                else if (Bearer.IsMatch(value)) Bump("bearer", sig.Url, $"hook: {name}");
                else if (LooksLikeHex(value, 32, 128) || name.Contains("sign", StringComparison.OrdinalIgnoreCase))
                    Bump("hmac_sha256", sig.Url, $"hook: {name}=<hex>");
            }
        }

        var top = counts.OrderByDescending(kv => kv.Value).FirstOrDefault();
        var scheme = top.Value == 0 ? "none" : top.Key;
        return new SignatureVerdict(scheme, evidence, examples);
    }

    private static bool LooksLikeHex(string s, int minLen, int maxLen)
    {
        if (string.IsNullOrEmpty(s) || s.Length < minLen || s.Length > maxLen) return false;
        foreach (var c in s) if (!Uri.IsHexDigit(c)) return false;
        return true;
    }
}
