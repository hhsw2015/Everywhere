using Everywhere.Mcp.OpenCli.Gates;
using Everywhere.Mcp.OpenCli.Memory;

namespace Everywhere.Mcp.Tests.Gates;

/// <summary>Regression tests for round-2 findings on VerifyFixtureGate.</summary>
[TestFixture]
public sealed class VerifyFixtureRegressionTests
{
    private static VerifyFixture MakeFixture(string col, string pattern) => new()
    {
        Cmd = "top", ExpectedRowCountMin = 1, ExpectedRowCountMax = 30,
        Patterns = new() { [col] = pattern },
        NotEmpty = new() { col },
        MustBeTruthy = new() { col },
        MustNotContain = new() { [col] = new() { "" } },
    };

    // Round-2: numeric literal ≥5 must be caught (spec says alnum, not just letters)
    [Test]
    public void G9_NumericLiteral_Rejected()
    {
        var r = VerifyFixtureGate.Check(MakeFixture("count", "^12345$"));
        Assert.That(r.Errors.Any(e => e.Code == "LITERAL_PATTERN_REJECTED"), Is.True);
    }

    // Round-2: escaped `\]` inside a char class must not launder literals.
    [Test]
    public void G9_EscapedCloseBracket_InCharClass_HandledCorrectly()
    {
        // Pattern with a real char-class containing an escaped ] followed by a literal run
        // outside the class: [\]abc]LiteralWord — the class strips clean, LiteralWord remains.
        var r = VerifyFixtureGate.Check(MakeFixture("q", @"[\]abc]HackerNewsSite"));
        Assert.That(r.Errors.Any(e => e.Code == "LITERAL_PATTERN_REJECTED"), Is.True,
            "literal 'HackerNewsSite' outside a char class must trip the gate");
    }

    // Round-1 spot-check (regression against re-broken behavior)
    [Test]
    public void G9_StructuralOnly_Passes()
    {
        var r = VerifyFixtureGate.Check(MakeFixture("id", "^[A-Za-z0-9]{8}$"));
        Assert.That(r.Ok, Is.True);
    }
}
