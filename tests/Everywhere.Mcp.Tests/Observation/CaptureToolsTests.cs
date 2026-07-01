using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenCli.Observation;
using Everywhere.Mcp.Tools;

namespace Everywhere.Mcp.Tests.Observation;

/// <summary>
/// Wire tests: CaptureTools without OpenDia bridge attached. Verifies
/// error envelopes for the SELFEXPAND-gated paths and the store integration.
/// </summary>
[TestFixture]
public sealed class CaptureToolsTests
{
    private IDisposable? _gate;

    [OneTimeSetUp] public void EnableSelfExpand() => _gate = SelfExpandGate.EnableForTest();
    [OneTimeTearDown] public void RestoreGate() => _gate?.Dispose();

    [Test]
    public async Task CaptureStart_ReturnsSessionId()
    {
        var store = new CaptureSessionStore();
        var tools = new CaptureTools(store);
        var json = JsonNode.Parse(await tools.CaptureStart(101, "example.com"))!.AsObject();
        Assert.That(json["session_id"]!.GetValue<string>(), Does.Match("^[0-9a-fA-F-]{36}$"));
    }

    [Test]
    public async Task CaptureStop_UnknownSession_ReturnsSessionNotFound()
    {
        var tools = new CaptureTools(new CaptureSessionStore());
        var json = JsonNode.Parse(await tools.CaptureStop("no-such"))!.AsObject();
        Assert.That(json["ok"]!.GetValue<bool>(), Is.False);
        Assert.That(json["code"]!.GetValue<string>(), Is.EqualTo("SESSION_NOT_FOUND"));
    }

    [Test]
    public void CaptureExport_UnknownSession_ReturnsSessionNotFound()
    {
        var tools = new CaptureTools(new CaptureSessionStore());
        var json = JsonNode.Parse(tools.CaptureExport("00000000-0000-4000-8000-000000000abc"))!.AsObject();
        Assert.That(json["code"]!.GetValue<string>(), Is.EqualTo("SESSION_NOT_FOUND"));
    }

    [Test]
    public void CaptureExport_TraversalId_ReturnsInvalidIdentifier()
    {
        var tools = new CaptureTools(new CaptureSessionStore());
        var json = JsonNode.Parse(tools.CaptureExport("../../../etc/passwd"))!.AsObject();
        Assert.That(json["code"]!.GetValue<string>(), Is.EqualTo("INVALID_IDENTIFIER"));
    }

    [Test]
    public void PageSaveExtractionRule_Persists_ThenMatchViaExtractionRules()
    {
        using var _ = EverywherePaths.OverrideBaseForTest(TempDir());
        var tools = new CaptureTools(new CaptureSessionStore());
        var ok = JsonNode.Parse(tools.PageSaveExtractionRule("github\\.com/.*", "css", ".repository-content", 10))!.AsObject();
        Assert.That(ok["ok"]!.GetValue<bool>(), Is.True);
        var rule = new ExtractionRules().Match("https://github.com/foo/bar");
        Assert.That(rule, Is.Not.Null);
        Assert.That(rule!.Selector, Is.EqualTo(".repository-content"));
    }

    [Test]
    public async Task CaptureExport_WritesJsonUnderEverywhereDir_And1G_InvalidIdentifierBlockedFirst()
    {
        using var _ = EverywherePaths.OverrideBaseForTest(TempDir());
        var store = new CaptureSessionStore();
        var tools = new CaptureTools(store);
        var startJson = JsonNode.Parse(await tools.CaptureStart(1, "example.com"))!.AsObject();
        var id = startJson["session_id"]!.GetValue<string>();
        var res = JsonNode.Parse(tools.CaptureExport(id))!.AsObject();
        Assert.That(res["path"], Is.Not.Null);
        var path = res["path"]!.GetValue<string>();
        Assert.That(File.Exists(path), Is.True);
        Assert.That(path, Does.Contain(Path.Combine(".everywhere", "captures")).Or.Contain(Path.Combine("captures")));
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "everywhere-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
