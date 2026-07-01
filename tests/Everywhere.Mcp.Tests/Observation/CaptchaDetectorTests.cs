using Everywhere.Mcp.OpenCli.Observation;

namespace Everywhere.Mcp.Tests.Observation;

[TestFixture]
public sealed class CaptchaDetectorTests
{
    [Test]
    public void RecaptchaV2_DetectedFromSitekeyAnchor()
    {
        var html = "<div class='g-recaptcha' data-sitekey='6Le-wvkSAAAAAPBMRTvw0Q4Muexq9bi0DJwx_mJ-'></div>" +
                   "<iframe src='https://www.google.com/recaptcha/api2/anchor?k=abc'></iframe>";
        var r = CaptchaDetector.Detect(html);
        Assert.That(r.Present, Is.True);
        Assert.That(r.Kind, Is.EqualTo("recaptcha_v2"));
        Assert.That(r.Confidence, Is.GreaterThan(0.5));
    }

    [Test]
    public void RecaptchaV3_DetectedFromExecuteCall()
    {
        var html = "<script src='https://www.google.com/recaptcha/api.js?render=6Le-xyz'></script>" +
                   "<script>grecaptcha.execute('6Le-xyz', {action:'homepage'})</script>";
        var r = CaptchaDetector.Detect(html);
        Assert.That(r.Present, Is.True);
        Assert.That(r.Kind, Is.EqualTo("recaptcha_v3"));
    }

    [Test]
    public void Turnstile_DetectedFromCookieAndMarkup()
    {
        var html = "<div class='cf-turnstile' data-sitekey='0x000'></div>";
        var r = CaptchaDetector.Detect(html, new[] { "__cf_bm" });
        Assert.That(r.Present, Is.True);
        Assert.That(r.Kind, Is.EqualTo("cloudflare_turnstile"));
        Assert.That(r.Confidence, Is.GreaterThan(0.7));
    }

    [Test]
    public void HCaptcha_DetectedFromMarkup()
    {
        var html = "<div class='h-captcha' data-sitekey='k'></div>";
        var r = CaptchaDetector.Detect(html);
        Assert.That(r.Present, Is.True);
        Assert.That(r.Kind, Is.EqualTo("hcaptcha"));
    }

    [Test]
    public void NoCaptcha_ReturnsFalse()
    {
        var r = CaptchaDetector.Detect("<html><body>hello</body></html>");
        Assert.That(r.Present, Is.False);
        Assert.That(r.Kind, Is.Null);
    }
}
