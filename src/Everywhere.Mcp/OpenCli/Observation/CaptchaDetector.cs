using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Everywhere.Mcp.OpenCli.Observation;

/// <summary>
/// Port of jshookmcp CaptchaDetector heuristics. Operates on the DOM
/// snapshot text (HTML fragment / a11y tree / iframe src set) plus
/// cookies list. Each detector returns null when confidence is 0.
/// The MCP handler wires <c>browser_snapshot</c> + <c>browser_cookies_get</c>
/// into <see cref="Detect"/> and picks the highest-confidence result.
/// </summary>
public static class CaptchaDetector
{
    public enum Kind { RecaptchaV2, RecaptchaV3, CloudflareTurnstile, HCaptcha }

    public sealed record Result(bool Present, string? Kind, double Confidence);

    private static readonly Regex RcV2Sitekey = new(
        @"data-sitekey\s*=\s*['""][^'""]+['""]|g-recaptcha-response",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RcAnchorIframe = new(
        @"recaptcha/api2/anchor|www\.google\.com/recaptcha",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RcV3Js = new(
        @"grecaptcha\.execute\s*\(|render\s*=\s*['""]explicit['""]|recaptcha/api\.js\?render=",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TurnstileMark = new(
        @"cf-turnstile|challenges\.cloudflare\.com/turnstile|__cf_bm",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HCaptchaMark = new(
        @"h-captcha|hcaptcha\.com|hcaptcha-invisible",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static Result Detect(string domHtml, IEnumerable<string>? cookieNames = null)
    {
        cookieNames ??= [];
        var cookieSet = new HashSet<string>(cookieNames, StringComparer.OrdinalIgnoreCase);

        var v2 = DetectRecaptchaV2(domHtml);
        var v3 = DetectRecaptchaV3(domHtml);
        var cf = DetectCloudflareTurnstile(domHtml, cookieSet);
        var hc = DetectHCaptcha(domHtml);
        var best = new[] { v2, v3, cf, hc }
            .Where(r => r is not null)
            .OrderByDescending(r => r!.Confidence)
            .FirstOrDefault();
        return best ?? new Result(false, null, 0);
    }

    public static Result? DetectRecaptchaV2(string html)
    {
        var scoreV2 = 0.0;
        if (RcV2Sitekey.IsMatch(html)) scoreV2 += 0.5;
        if (RcAnchorIframe.IsMatch(html)) scoreV2 += 0.4;
        if (Regex.IsMatch(html, "class=['\"](\\s|\\w|-)*g-recaptcha", RegexOptions.IgnoreCase)) scoreV2 += 0.2;
        return scoreV2 > 0 ? new Result(true, "recaptcha_v2", Math.Min(scoreV2, 1.0)) : null;
    }

    public static Result? DetectRecaptchaV3(string html)
    {
        if (!RcV3Js.IsMatch(html)) return null;
        // Distinguishing v3 from v2 hint: v3 typically has no anchor iframe on-page.
        var confidence = 0.6;
        if (Regex.IsMatch(html, @"recaptcha/api\.js\?render=[A-Za-z0-9_-]+", RegexOptions.IgnoreCase))
            confidence = 0.85;
        return new Result(true, "recaptcha_v3", confidence);
    }

    public static Result? DetectCloudflareTurnstile(string html, ISet<string> cookieNames)
    {
        var score = 0.0;
        if (TurnstileMark.IsMatch(html)) score += 0.6;
        if (cookieNames.Contains("__cf_bm") || cookieNames.Contains("cf_clearance")) score += 0.3;
        return score > 0 ? new Result(true, "cloudflare_turnstile", Math.Min(score, 1.0)) : null;
    }

    public static Result? DetectHCaptcha(string html)
    {
        if (!HCaptchaMark.IsMatch(html)) return null;
        return new Result(true, "hcaptcha", 0.8);
    }
}
