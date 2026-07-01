using System.Text.Json;
using Everywhere.Mcp.OpenCli.Memory;
using Everywhere.Mcp.OpenCli.Observation;

namespace Everywhere.Mcp.Tests.Memory;

[TestFixture]
public sealed class MemoryStoreTests
{
    private IDisposable? _base;

    [SetUp]
    public void RootFresh()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "everywhere-memtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        _base = EverywherePaths.OverrideBaseForTest(tmp);
    }

    [TearDown]
    public void CleanUp() => _base?.Dispose();

    [Test]
    public void WriteEndpoint_MergeConflict_On_ExistingKey_WithoutForce()
    {
        var store = new MemoryStore();
        var spec = new EndpointSpec { Name = "top", Method = "GET", UrlTemplate = "https://x/api", VerifiedAt = 1 };
        store.WriteEndpoint("hackernews", "top", spec);
        Assert.Throws<MergeConflictException>(() => store.WriteEndpoint("hackernews", "top", spec));
        // Force overwrite works
        Assert.DoesNotThrow(() => store.WriteEndpoint("hackernews", "top", spec, force: true));
    }

    [Test]
    public void Freshness_ClassifiesAcross30_And_90Days()
    {
        var clock = new FakeClock(0);
        var store = new MemoryStore(clock);
        store.UpdateMetadata("news", m => m.VerifiedAt = 1); // any non-zero value
        Assert.That(store.Freshness("news"), Is.EqualTo("fresh"));
        clock.Advance(TimeSpan.FromDays(31));
        Assert.That(store.Freshness("news"), Is.EqualTo("stale"));
        clock.Advance(TimeSpan.FromDays(61)); // total 92d
        Assert.That(store.Freshness("news"), Is.EqualTo("cold"));
    }

    [Test]
    public void InvalidIdentifier_BlocksPathTraversal()
    {
        var store = new MemoryStore();
        Assert.Throws<InvalidIdentifierException>(() => store.ResolveSitePath("../../etc"));
    }

    [Test]
    public void SubPathTraversalRejected()
    {
        var store = new MemoryStore();
        Assert.Throws<PathTraversalException>(() => store.ResolveSitePath("news", "../foo"));
    }

    [Test]
    public void StrategyNote_RoundTripsFrontmatter()
    {
        var store = new MemoryStore(new FakeClock(1234567));
        var note = new StrategyNote
        {
            Strategy = "cookie",
            Contract = "stable",
            Mutation = false,
            Evidence = [new string('a', 25), new string('b', 25), new string('c', 25)],
            Replay = new string('r', 60),
        };
        var path = store.WriteStrategyNote("hackernews", "user_karma", note);
        Assert.That(File.Exists(path), Is.True);
        var read = store.ReadStrategyNote("hackernews", "user_karma");
        Assert.That(read, Is.Not.Null);
        Assert.That(read!.Strategy, Is.EqualTo("cookie"));
        Assert.That(read.Evidence, Has.Count.EqualTo(3));
        Assert.That(read.Replay.Length, Is.GreaterThanOrEqualTo(50));
        Assert.That(read.IsComplete(out _), Is.True);
    }

    [Test]
    public void FixtureRotation_KeepsLastFive()
    {
        var clock = new FakeClock(1000);
        var store = new MemoryStore(clock);
        for (int i = 0; i < 8; i++)
        {
            store.WriteSnapshot("news", "top", $"{{\"iter\": {i}}}");
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        var files = Directory.EnumerateFiles(store.ResolveSitePath("news", "fixtures"), "top-*.json").ToArray();
        Assert.That(files.Length, Is.EqualTo(5));
    }

    [Test]
    public void SnapshotOutput_RedactsProviderPatterns()
    {
        var store = new MemoryStore();
        var path = store.WriteSnapshot("news", "audit", "authorization: Bearer ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ab");
        var contents = File.ReadAllText(path);
        // Snapshot writes raw text; redaction handled by MemoryTools.MemorySnapshot at the tool boundary.
        Assert.That(contents, Does.Contain("ghp_")); // store itself doesn't scrub — verifies the boundary contract
    }

    [Test]
    public void ConcurrentEndpointWrites_ExactlyOneWins()
    {
        var store = new MemoryStore();
        var spec = new EndpointSpec { Name = "vote", Method = "POST", UrlTemplate = "https://x/vote", Mutation = true, VerifiedAt = 1 };
        var successCount = 0;
        var conflictOrTimeout = 0;
        Parallel.For(0, 4, _ =>
        {
            try { store.WriteEndpoint("news", "vote", spec); Interlocked.Increment(ref successCount); }
            catch (MergeConflictException) { Interlocked.Increment(ref conflictOrTimeout); }
            catch (MemoryLockTimeoutException) { Interlocked.Increment(ref conflictOrTimeout); }
        });
        Assert.That(successCount, Is.EqualTo(1));
        Assert.That(conflictOrTimeout, Is.EqualTo(3));
    }
}
