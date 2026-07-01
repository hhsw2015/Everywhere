using Everywhere.Mcp.OpenCli.Gates;
using Everywhere.Mcp.OpenCli.Memory;

namespace Everywhere.Mcp.Tests.Gates;

/// <summary>Regression tests for review-round-1 findings F9/F10/F17/F18.</summary>
[TestFixture]
public sealed class GateRegressionsTests
{
    // F9 — LITERAL_PATTERN must not short-circuit on any single .* / .+
    [Test]
    public void G9_LiteralPatternGluedByDotStar_StillRejected()
    {
        var fx = new VerifyFixture
        {
            Cmd = "top", ExpectedRowCountMin = 1, ExpectedRowCountMax = 30,
            Patterns = new() { ["title"] = "^HackerNews.*ycombinator$" },
            NotEmpty = new() { "title" },
            MustBeTruthy = new() { "title" },
            MustNotContain = new() { ["title"] = new() { "" } },
        };
        var r = VerifyFixtureGate.Check(fx);
        Assert.That(r.Errors.Any(e => e.Code == "LITERAL_PATTERN_REJECTED"), Is.True,
            "literal 'HackerNews' or 'ycombinator' must trip the gate even when glued by .*");
    }

    [Test]
    public void G9_CharClassAlnumRepetition_NotRejected()
    {
        var fx = new VerifyFixture
        {
            Cmd = "top", ExpectedRowCountMin = 1, ExpectedRowCountMax = 30,
            Patterns = new() { ["id"] = "^[A-Za-z0-9]{8}$" },
            NotEmpty = new() { "id" },
            MustBeTruthy = new() { "id" },
            MustNotContain = new() { ["id"] = new() { "" } },
        };
        var r = VerifyFixtureGate.Check(fx);
        Assert.That(r.Ok, Is.True,
            "[A-Za-z0-9]{8} is a structural pattern, must not trip literal check");
    }

    // F10 — ASI-form throw-new must be recognized as typed
    [Test]
    public void G4_AsiFormThrowNew_NotFalseFlagged()
    {
        var src = "func: async (args) => { throw\n  new EmptyResultError('done'); }";
        var r = TypedErrorLint.Check(src);
        Assert.That(r.Errors, Is.Empty,
            "throw\\n new EmptyResultError(...) is legal JS ASI and must not fail G4");
    }

    // F17 — dynamic method value should still trip mutation guard
    [Test]
    public void G7_DynamicMethodVar_TrippedByMutationGuard()
    {
        var src = "const m = 'PO' + 'ST'; fetch(url, {method: m, body: p});";
        Assert.That(AdapterSourceScan.HasMutationCall(src), Is.True,
            "dynamic method value must be treated as potentially mutating");
    }

    // F18 — Array.of() / new Array(0) / [...[]] must all fail G5
    [TestCase("func: async (args) => { return Array.of(); }")]
    [TestCase("func: async (args) => { return new Array(0); }")]
    [TestCase("func: async (args) => { return [...[]]; }")]
    public void G5_AlternateEmptyReturns_Rejected(string src)
    {
        var r = SilentFallbackLint.Check(src);
        Assert.That(r.Errors.Any(e => e.Code == "SILENT_FALLBACK_RETURN_EMPTY"), Is.True);
    }
}
