using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenCli.Observation;

namespace Everywhere.Mcp.Tests.Observation;

[TestFixture]
public sealed class RedactorTests
{
    [Test]
    public void Headers_RedactsSensitiveNames()
    {
        var headers = new Dictionary<string, string>
        {
            ["Cookie"] = "s=abc",
            ["Authorization"] = "Bearer xyz",
            ["X-Api-Key"] = "some-opaque-key-value-not-a-real-format",
            ["Content-Type"] = "application/json",
        };
        var result = Redactor.Headers(headers);
        Assert.Multiple(() =>
        {
            Assert.That(result["Cookie"], Does.StartWith("<REDACTED:"));
            Assert.That(result["Authorization"], Does.StartWith("<REDACTED:"));
            Assert.That(result["X-Api-Key"], Does.StartWith("<REDACTED:"));
            Assert.That(result["Content-Type"], Is.EqualTo("application/json"));
        });
    }

    [Test]
    public void Url_RedactsSensitiveQueryKeys()
    {
        var u = "https://api/example?token=abcXYZ&user=pg&api_key=zzz#f";
        var r = Redactor.Url(u);
        Assert.That(r, Does.Contain("token=<REDACTED>"));
        Assert.That(r, Does.Contain("api_key=<REDACTED>"));
        Assert.That(r, Does.Contain("user=pg"));
        Assert.That(r, Does.EndWith("#f"));
    }

    [Test]
    public void Body_RedactsJwtAndProviders()
    {
        var b = Redactor.Body("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abcXYZ and gho_1234567890abcdefghijklmnopqrstuvwxyz");
        Assert.That(b, Does.Contain("<REDACTED:JWT>"));
        Assert.That(b, Does.Contain("<REDACTED:GITHUB>"));
    }

    [Test]
    public void JsonBody_RedactsSensitiveKeysRecursively()
    {
        var node = JsonNode.Parse("{\"data\":{\"access_token\":\"secret\",\"user\":\"pg\"}}");
        var redacted = Redactor.JsonBody(node)!.AsObject();
        Assert.That(redacted["data"]!["access_token"]!.GetValue<string>(), Is.EqualTo("<REDACTED>"));
        Assert.That(redacted["data"]!["user"]!.GetValue<string>(), Is.EqualTo("pg"));
    }
}
