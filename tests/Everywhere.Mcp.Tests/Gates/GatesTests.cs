using System.Text.Json;
using Everywhere.Mcp.OpenCli.Gates;
using Everywhere.Mcp.OpenCli.Memory;
using Everywhere.Mcp.OpenCli.Observation;

namespace Everywhere.Mcp.Tests.Gates;

[TestFixture]
public sealed class GatesTests
{
    private IDisposable? _base;

    [SetUp]
    public void Setup()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "everywhere-gates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        _base = EverywherePaths.OverrideBaseForTest(tmp);
    }

    [TearDown]
    public void Teardown() => _base?.Dispose();

    private static string FixtureDir(string sub)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "tests", "Everywhere.Mcp.Tests", "Gates", "fixtures", sub);
            if (Directory.Exists(probe)) return probe;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"fixtures/{sub} not found near {AppContext.BaseDirectory}");
    }

    // ----- G1 / G2 (strategy note gate) -----

    [Test]
    public void G1_MissingStrategyNote_Fails()
    {
        var gate = new StrategyNoteGate(new MemoryStore());
        var r = gate.Check("news", "user_karma");
        Assert.That(r.Errors, Has.Count.EqualTo(1));
        Assert.That(r.Errors[0].Code, Is.EqualTo("STRATEGY_NOTE_MISSING"));
    }

    [Test]
    public void G2_IncompleteStrategyNote_Fails()
    {
        var store = new MemoryStore();
        store.WriteStrategyNote("news", "user_karma", new StrategyNote
        {
            Strategy = "public", Contract = "stable",
            Evidence = ["short"], Replay = "x",
        });
        var r = new StrategyNoteGate(store).Check("news", "user_karma");
        Assert.That(r.Errors[0].Code, Is.EqualTo("STRATEGY_NOTE_INCOMPLETE"));
    }

    // ----- G3-G8 fixture-driven -----

    [Test]
    public void G4_UntypedThrow_DetectsPlainError()
    {
        var src = File.ReadAllText(Path.Combine(FixtureDir("bad"), "untyped-throw-01.js"));
        var r = TypedErrorLint.Check(src);
        Assert.That(r.Errors.Any(e => e.Code == "UNTYPED_THROW"), Is.True);
    }

    [Test]
    public void G5_SilentFallback_DetectsReturnEmptyArray()
    {
        var src = File.ReadAllText(Path.Combine(FixtureDir("bad"), "silent-fallback-empty-01.js"));
        var r = SilentFallbackLint.Check(src);
        Assert.That(r.Errors.Any(e => e.Code == "SILENT_FALLBACK_RETURN_EMPTY"), Is.True);
    }

    [Test]
    public void G5_SentinelRow_Detected()
    {
        var src = File.ReadAllText(Path.Combine(FixtureDir("bad"), "sentinel-row-01.js"));
        var r = SilentFallbackLint.Check(src);
        Assert.That(r.Errors.Any(e => e.Code == "SENTINEL_ROW"), Is.True);
    }

    [Test]
    public void G6_MathMinArgs_Detected()
    {
        var src = File.ReadAllText(Path.Combine(FixtureDir("bad"), "external-arg-clamped-01.js"));
        var r = ClampLint.Check(src);
        Assert.That(r.Errors.Any(e => e.Code == "EXTERNAL_ARG_CLAMPED"), Is.True);
    }

    [Test]
    public void G3_BrowserTrueWithSingleArg_Fails()
    {
        var src = File.ReadAllText(Path.Combine(FixtureDir("bad"), "signature-form-mismatch-01.js"));
        var r = SignatureGuard.Check(src);
        Assert.That(r.Errors.Any(e => e.Code == "SIGNATURE_FORM_MISMATCH"), Is.True);
    }

    [Test]
    public void G7_PostEvidenceWithMutationFalse_Fails()
    {
        var note = new StrategyNote
        {
            Strategy = "cookie", Contract = "stable",
            Evidence = [
                "POST /vote endpoint returns 200 and updates score",
                "server session cookie required for auth path",
                "response body is plain-text 'ok' after mutation",
            ],
            Replay = "click upvote arrow → POST /vote?id=... issued from browser DevTools captures the auth cookie",
            Mutation = false,
        };
        var r = MutationGuard.Check(note, "const ok = 1;");
        Assert.That(r.Errors.Any(e => e.Code == "MUTATION_UNAPPROVED"), Is.True);
    }

    // ----- G9 verify-fixture gate -----

    [Test]
    public void G9_MissingMustNotContain_ReturnsFixtureIncomplete()
    {
        var fx = new VerifyFixture
        {
            Cmd = "top", ExpectedRowCountMin = 1, ExpectedRowCountMax = 30,
            Patterns = { ["id"] = "^\\d+$" },
            NotEmpty = { "id" },
            MustBeTruthy = { "id" },
            // Missing MustNotContain
        };
        var r = VerifyFixtureGate.Check(fx);
        Assert.That(r.Errors.Any(e => e.Code == "VERIFY_FIXTURE_INCOMPLETE"), Is.True);
    }

    [Test]
    public void G9_LiteralPattern_Rejected()
    {
        var fx = new VerifyFixture
        {
            Cmd = "top", ExpectedRowCountMin = 1, ExpectedRowCountMax = 30,
            Patterns = { ["title"] = "^Ask HN: Something" },
            NotEmpty = { "title" },
            MustBeTruthy = { "title" },
            MustNotContain = new Dictionary<string, List<string>> { ["title"] = new() { "" } },
        };
        var r = VerifyFixtureGate.Check(fx);
        Assert.That(r.Errors.Any(e => e.Code == "LITERAL_PATTERN_REJECTED"), Is.True);
    }

    [Test]
    public void G9_StructuralPattern_Passes()
    {
        var fx = new VerifyFixture
        {
            Cmd = "top", ExpectedRowCountMin = 1, ExpectedRowCountMax = 30,
            Patterns = { ["title"] = "^.{1,300}$", ["karma"] = "^\\d+$" },
            NotEmpty = { "title" },
            MustBeTruthy = { "title", "karma" },
            MustNotContain = new Dictionary<string, List<string>> { ["title"] = new() { "" } },
        };
        var r = VerifyFixtureGate.Check(fx);
        Assert.That(r.Ok, Is.True);
    }

    // ----- good/*.js passes end-to-end -----

    [Test]
    public void GoodAdapter_PassesAllInline()
    {
        var src = File.ReadAllText(Path.Combine(FixtureDir("good"), "typed-throws-01.js"));
        var linter = new AdapterLinter();
        var r = linter.Lint(src);
        Assert.That(r.Ok, Is.True, string.Join('\n', r.Errors.Select(e => $"{e.Code}: {e.Message}")));
    }

    [Test]
    public void GoodBrowserAdapter_PassesAllInline()
    {
        var src = File.ReadAllText(Path.Combine(FixtureDir("good"), "browser-adapter-01.js"));
        var linter = new AdapterLinter();
        var r = linter.Lint(src);
        Assert.That(r.Ok, Is.True, string.Join('\n', r.Errors.Select(e => $"{e.Code}: {e.Message}")));
    }
}
