using Everywhere.Mcp.OpenCli.Analysis;
using Everywhere.Mcp.OpenCli.Observation;

namespace Everywhere.Mcp.Tests.Analysis;

[TestFixture]
public sealed class JsIndexAndCryptoTests
{
    [Test]
    public void JsIndex_ReturnsMatchWithRedactedSnippet()
    {
        var idx = new JsIndex();
        idx.Add("https://x.com/app.js",
            "const t = 'gho_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ab'; function signature(r) { return hmac(r); }");
        var hits = idx.Search("signature");
        Assert.That(hits, Is.Not.Empty);
        Assert.That(hits[0].Snippet, Does.Not.Contain("gho_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ab"));
    }

    [Test]
    public void CryptoScan_FindsCryptoJsHmac()
    {
        var body = "const sig = CryptoJS.HmacSHA256(payload, key).toString();";
        var hits = CryptoScan.Scan(body);
        Assert.That(hits, Has.Count.GreaterThan(0));
        Assert.That(hits[0].Algo, Is.EqualTo("hmac_sha256"));
    }
}
