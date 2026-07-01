using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenCli.Memory;
using Everywhere.Mcp.OpenCli.Observation;
using Everywhere.Mcp.Tools;

namespace Everywhere.Mcp.Tests.Memory;

[TestFixture]
public sealed class MemoryToolsTests
{
    private IDisposable? _base;
    private IDisposable? _gate;

    [SetUp]
    public void Setup()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "everywhere-mtools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        _base = EverywherePaths.OverrideBaseForTest(tmp);
        _gate = SelfExpandGate.EnableForTest();
    }

    [TearDown]
    public void Cleanup()
    {
        _base?.Dispose();
        _gate?.Dispose();
    }

    [Test]
    public void MemoryReadEndpoint_InvalidIdentifier()
    {
        var tools = new MemoryTools(new MemoryStore());
        var r = JsonNode.Parse(tools.MemoryReadEndpoint("../../etc", "top"))!.AsObject();
        Assert.That(r["code"]!.GetValue<string>(), Is.EqualTo("INVALID_IDENTIFIER"));
    }

    [Test]
    public void WriteAndRead_EndpointRoundTrip()
    {
        var tools = new MemoryTools(new MemoryStore());
        var spec = JsonSerializer.Serialize(new EndpointSpec
        {
            Name = "top", Method = "GET", UrlTemplate = "https://x/api", VerifiedAt = 42,
        });
        var write = JsonNode.Parse(tools.MemoryWriteEndpoint("news", "top", spec))!.AsObject();
        Assert.That(write["ok"]!.GetValue<bool>(), Is.True);
        // Double write w/o force -> MERGE_CONFLICT
        var second = JsonNode.Parse(tools.MemoryWriteEndpoint("news", "top", spec))!.AsObject();
        Assert.That(second["code"]!.GetValue<string>(), Is.EqualTo("MERGE_CONFLICT"));
        // Force writes cleanly
        var force = JsonNode.Parse(tools.MemoryWriteEndpoint("news", "top", spec, force: true))!.AsObject();
        Assert.That(force["ok"]!.GetValue<bool>(), Is.True);
    }

    [Test]
    public void MemorySnapshot_ScrubsAuthTokens()
    {
        var tools = new MemoryTools(new MemoryStore());
        var res = JsonNode.Parse(tools.MemorySnapshot("news", "audit",
            "content: token gho_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ab and eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abcXYZDEF"))!.AsObject();
        var path = res["path"]!.GetValue<string>();
        var text = File.ReadAllText(path);
        Assert.That(text, Does.Not.Contain("gho_ABCDEFGHIJKLMNOPQRSTUVWXYZ"));
        Assert.That(text, Does.Not.Contain(".eyJzdWIiOiIxIn0"));
        Assert.That(text, Does.Contain("<REDACTED"));
    }
}
