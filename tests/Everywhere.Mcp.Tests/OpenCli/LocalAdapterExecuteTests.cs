using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenCli;
using Everywhere.Mcp.OpenCli.Generator;
using Everywhere.Mcp.OpenCli.Observation;

namespace Everywhere.Mcp.Tests.OpenCli;

/// <summary>
/// End-to-end regression for F2 / R2-1: a locally-saved adapter must
/// actually execute through OpenCliRuntime.InvokeAsync.
/// </summary>
[TestFixture]
public sealed class LocalAdapterExecuteTests
{
    private IDisposable? _base;
    private string _root = "";

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "ew-runtime-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _base = EverywherePaths.OverrideBaseForTest(_root);
    }

    [TearDown]
    public void Teardown() => _base?.Dispose();

    [Test]
    public async Task LocalAdapter_ExecutesAndReturnsRow()
    {
        // Prepare a minimal local adapter.
        var src = @"
import { cli, Strategy } from '@jackwener/opencli/registry';
cli({
  site: 'testlocal',
  name: 'echo',
  description: 'echoes args',
  strategy: Strategy.PUBLIC,
  browser: false,
  args: [{ name: 'val', type: 'string' }],
  columns: ['val'],
  func: async (args) => [{ val: String(args && args.val || 'default') }],
});
";
        var path = LocalRegistry.ResolvePath("testlocal", "echo");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, src);

        // Locate the vendored 3rd/opencli/ tree so the runtime can boot.
        var repoDir = new DirectoryInfo(AppContext.BaseDirectory);
        while (repoDir is not null && !Directory.Exists(Path.Combine(repoDir.FullName, "3rd", "opencli", "clis")))
            repoDir = repoDir.Parent;
        Assert.That(repoDir, Is.Not.Null, "vendored opencli clis dir not found");

        var runtime = new OpenCliRuntime(
            clisDir: Path.Combine(repoDir!.FullName, "3rd", "opencli", "clis"),
            manifestPath: Path.Combine(repoDir!.FullName, "3rd", "opencli", "cli-manifest.json"),
            http: new HttpClient(),
            log: null);

        var res = await runtime.InvokeAsync("testlocal", "echo", new JsonObject { ["val"] = "hello" }, new Phase1StubPage(), CancellationToken.None);

        Assert.That(res, Is.Not.Null);
        Assert.That(res["ok"]!.GetValue<bool>(), Is.True, res.ToJsonString());
        var rows = res["data"] as JsonArray;
        Assert.That(rows, Is.Not.Null, res.ToJsonString());
        Assert.That(rows!.Count, Is.EqualTo(1));
        Assert.That(rows[0]!["val"]!.GetValue<string>(), Is.EqualTo("hello"));
    }
}
