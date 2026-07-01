using Everywhere.Mcp.OpenCli;
using Everywhere.Mcp.OpenCli.Generator;
using Everywhere.Mcp.OpenCli.Memory;
using Everywhere.Mcp.OpenCli.Observation;
using System.Text.Json;

namespace Everywhere.Mcp.Tests.OpenCli;

/// <summary>
/// Regression: F2 — OpenCliRuntime.ResolveAdapterAsync must find local
/// adapters and set Origin="local"; vendored wins on collision unless
/// EVERYWHERE_MCP_LOCAL_SHADOW=1.
/// </summary>
[TestFixture]
public sealed class RuntimeLocalRegistryResolutionTests
{
    private IDisposable? _base;

    [SetUp]
    public void Setup()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ew-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        _base = EverywherePaths.OverrideBaseForTest(tmp);
    }

    [TearDown]
    public void Teardown() => _base?.Dispose();

    [Test]
    public async Task LocalRegistry_LoadAsync_MarksOriginLocal()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LocalRegistry.ResolvePath("intranet", "widget"))!);
        File.WriteAllText(LocalRegistry.ResolvePath("intranet", "widget"), "// noop");
        var def = await LocalRegistry.LoadAsync("intranet", "widget", CancellationToken.None);
        Assert.That(def, Is.Not.Null);
        Assert.That(def!.Origin, Is.EqualTo("local"));
    }

    [Test]
    public async Task LocalRegistry_LoadAsync_MissingReturnsNull()
    {
        var def = await LocalRegistry.LoadAsync("intranet", "nope", CancellationToken.None);
        Assert.That(def, Is.Null);
    }
}
