using Everywhere.Mcp.OpenCli.Analysis;
using Everywhere.Mcp.OpenCli.Observation;

namespace Everywhere.Mcp.Tests.Analysis;

[TestFixture]
public sealed class SignatureAndTechStackTests
{
    [Test]
    public void SignatureScheme_DetectsBearerJwt()
    {
        var session = new CaptureSession { Origin = "api.example.com", SessionId = "s1" };
        session.Network.Requests.Add(new CaptureSession.NetworkRequest
        {
            RequestId = "a",
            Url = "https://api.example.com/me",
            Method = "GET",
            Status = 200,
            RequestHeaders = new(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer eyJhbGciOiJIUzI1NiJ9.something" },
        });
        var v = SignatureScheme.Detect(session);
        Assert.That(v.Scheme, Is.EqualTo("jwt"));
        Assert.That(v.Evidence, Is.Not.Empty);
    }

    [Test]
    public void SignatureScheme_DetectsHmacHeader()
    {
        var session = new CaptureSession { Origin = "api.example.com", SessionId = "s1" };
        session.Network.Requests.Add(new CaptureSession.NetworkRequest
        {
            RequestId = "a",
            Url = "https://api.example.com/me",
            RequestHeaders = new(StringComparer.OrdinalIgnoreCase) { ["X-Signature"] = "sha256=..." },
        });
        Assert.That(SignatureScheme.Detect(session).Scheme, Is.EqualTo("hmac_sha256"));
    }

    [Test]
    public void TechStack_DetectsReactAndNext()
    {
        var session = new CaptureSession { Origin = "x.com", SessionId = "s" };
        session.Network.Requests.Add(new CaptureSession.NetworkRequest
        {
            RequestId = "a",
            Url = "https://x.com/_next/static/react@18.2.0/react-dom.js",
            ResponseBodySha256 = "h",
            ResponseContentType = "application/javascript",
        });
        session.Network.BodiesByHash["h"] = "window.__NEXT_DATA__ = { props: {} };";
        var t = TechStack.Detect(session);
        Assert.That(t.Framework, Is.EqualTo("react"));
        Assert.That(t.FrameworkVersion, Is.EqualTo("18.2.0"));
        Assert.That(t.BuildTool, Is.EqualTo("next.js"));
    }
}
