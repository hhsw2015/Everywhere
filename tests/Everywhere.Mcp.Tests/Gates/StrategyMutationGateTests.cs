using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenCli.Memory;
using Everywhere.Mcp.OpenCli.Observation;
using Everywhere.Mcp.Tools;

namespace Everywhere.Mcp.Tests.Gates;

/// <summary>Regression: F8 — strategy_note_write must reject non-mutation notes with mutating verb evidence.</summary>
[TestFixture]
public sealed class StrategyMutationGateTests
{
    private IDisposable? _base;
    private IDisposable? _gate;

    [SetUp]
    public void Setup()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ew-mut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        _base = EverywherePaths.OverrideBaseForTest(tmp);
        _gate = SelfExpandGate.EnableForTest();
    }

    [TearDown]
    public void Teardown() { _base?.Dispose(); _gate?.Dispose(); }

    [Test]
    public void PostEvidenceWithoutMutationFlag_RejectedAtWrite()
    {
        var note = new StrategyNote
        {
            Strategy = "cookie", Contract = "stable", Mutation = false,
            Evidence = new List<string>
            {
                "POST /vote endpoint returns 200 and updates score for the current user",
                "server session cookie required for the auth path to succeed cleanly",
                "response body is plain-text 'ok' after the mutation completes normally",
            },
            Replay = "click upvote arrow → POST /vote?id=... issued from the browser DevTools captures the auth cookie",
        };
        var tools = new GateTools(new MemoryStore());
        var res = JsonNode.Parse(tools.StrategyNoteWrite("news", "vote", JsonSerializer.Serialize(note)))!.AsObject();
        Assert.That(res["code"]!.GetValue<string>(), Is.EqualTo("MUTATION_UNAPPROVED"));
    }
}
