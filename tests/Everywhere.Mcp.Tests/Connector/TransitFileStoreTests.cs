// SPEC docs/specs/everywhere-connector.md §6 Phase 8 — TransitFileStore
// end-to-end test. Exercises Create → TryRead → Delete on the C# side
// so a regression in the base64 bridge or file layout fires locally
// (no HTTP).

using System.Text;
using System.Text.Json.Nodes;
using Everywhere.Mcp.Connector;

namespace Everywhere.Mcp.Tests.Connector;

[TestFixture]
public class TransitFileStoreTests
{
    private string _tmpDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "everywhere-transit-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    private TransitFileStore MakeStore()
        => new TransitFileStore(baseUrlFactory: () => "http://127.0.0.1:7878", overrideDir: _tmpDir);

    [Test]
    public void Create_Then_Read_RoundTrips()
    {
        var store = MakeStore();
        var payload = Encoding.UTF8.GetBytes("hello transit");
        var meta = store.Create(payload, "greeting.txt", "text/plain");

        Assert.That(meta["fileId"]!.GetValue<string>().Length, Is.EqualTo(32));
        Assert.That(meta["sizeBytes"]!.GetValue<int>(), Is.EqualTo(payload.Length));
        Assert.That(meta["downloadUrl"]!.GetValue<string>(),
            Does.StartWith("http://127.0.0.1:7878/v1/files/"));

        var fileId = meta["fileId"]!.GetValue<string>();
        Assert.That(store.TryRead(fileId, out var bytes, out var name, out var mime), Is.True);
        Assert.That(bytes, Is.EqualTo(payload));
        Assert.That(name, Is.EqualTo("greeting.txt"));
        Assert.That(mime, Is.EqualTo("text/plain"));
    }

    [Test]
    public void Read_UnsafeId_Rejected()
    {
        var store = MakeStore();
        // Path-traversal attempts must not reach disk.
        Assert.That(store.TryRead("../etc/passwd", out _, out _, out _), Is.False);
        Assert.That(store.TryRead("aaa", out _, out _, out _), Is.False);
        Assert.That(store.TryRead(new string('z', 32), out _, out _, out _), Is.False);
    }

    [Test]
    public void Delete_RemovesBothBlobAndMeta()
    {
        var store = MakeStore();
        var meta = store.Create(new byte[] { 1, 2, 3 }, "x.bin", "application/octet-stream");
        var fileId = meta["fileId"]!.GetValue<string>();

        Assert.That(store.Delete(fileId), Is.True);
        Assert.That(store.TryRead(fileId, out _, out _, out _), Is.False);
        // Idempotent — second delete returns false.
        Assert.That(store.Delete(fileId), Is.False);

        // Both files must be gone from the tempdir.
        var remaining = Directory.EnumerateFiles(_tmpDir, fileId + "*").ToArray();
        Assert.That(remaining, Is.Empty);
    }

    [Test]
    public void Create_ExceedingCap_Throws()
    {
        var store = MakeStore();
        var oversized = new byte[store.MaxBytes + 1];
        Assert.Throws<InvalidOperationException>(
            () => store.Create(oversized, "big.bin", "application/octet-stream"));
    }
}
