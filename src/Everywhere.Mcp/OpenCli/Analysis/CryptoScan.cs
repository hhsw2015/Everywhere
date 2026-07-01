using System.Text.RegularExpressions;

namespace Everywhere.Mcp.OpenCli.Analysis;

/// <summary>SPEC §Phase 2 — regex scan for crypto / encoding APIs in JS bodies. Port of jshookmcp CryptoDetector.</summary>
public sealed record CryptoHit(string Algo, string Api, string Strength, string Snippet);

public static class CryptoScan
{
    private static readonly (Regex Rx, string Algo, string Api, string Strength)[] Signals =
    [
        (new Regex(@"crypto\.subtle\.digest\s*\(\s*['""](SHA-256)['""]", RegexOptions.IgnoreCase), "sha256", "crypto.subtle", "modern"),
        (new Regex(@"CryptoJS\.HmacSHA256", RegexOptions.IgnoreCase), "hmac_sha256", "CryptoJS", "modern"),
        (new Regex(@"CryptoJS\.AES\.(encrypt|decrypt)", RegexOptions.IgnoreCase), "aes", "CryptoJS", "modern"),
        (new Regex(@"CryptoJS\.MD5", RegexOptions.IgnoreCase), "md5", "CryptoJS", "weak"),
        (new Regex(@"crypto\.createHmac\s*\(\s*['""]sha256['""]", RegexOptions.IgnoreCase), "hmac_sha256", "node:crypto", "modern"),
        (new Regex(@"btoa\s*\(", RegexOptions.IgnoreCase), "base64", "btoa", "encoding_only"),
    ];

    public static List<CryptoHit> Scan(string jsBody)
    {
        var results = new List<CryptoHit>();
        foreach (var (rx, algo, api, strength) in Signals)
        {
            foreach (Match m in rx.Matches(jsBody))
            {
                var start = Math.Max(0, m.Index - 40);
                var end = Math.Min(jsBody.Length, m.Index + m.Length + 40);
                results.Add(new CryptoHit(algo, api, strength, jsBody[start..end]));
            }
        }
        return results;
    }
}
