using System.Text.RegularExpressions;
using Everywhere.Mcp.OpenCli.Memory;

namespace Everywhere.Mcp.OpenCli.Gates;

/// <summary>
/// SPEC §Phase 4 G9 / §10.10 — verify fixture must have ≥1 entry in
/// each of the 4 tuple fields. Patterns must be structural, not literal:
/// reject regex containing literal alnum ≥5 chars unless anchored with
/// `.*` / `.+`.
/// </summary>
public static class VerifyFixtureGate
{
    // Literal run = 5+ alphanumeric or CJK characters that aren't inside
    // a character class. SPEC §10.10 says "alnum ≥5" → include digits so
    // `"12345"` isn't laundered.
    private static readonly Regex LiteralRun = new(
        @"[A-Za-z0-9一-鿿]{5,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex StructuralGlue = new(
        @"\.\*|\.\+",
        RegexOptions.Compiled);

    // Character class stripper that respects `\]` inside the class body.
    private static readonly Regex CharClass = new(
        @"\[(?:\\.|[^\]\\])*\]",
        RegexOptions.Compiled);

    public static GateResult Check(VerifyFixture fixture)
    {
        var r = GateResult.Empty();
        if (!fixture.Is4TupleComplete(out var missing))
        {
            r.Errors.Add(new GateFinding("G9", "VERIFY_FIXTURE_INCOMPLETE",
                "fixture missing 4-tuple fields: " + string.Join(", ", missing)));
        }
        foreach (var (col, pattern) in fixture.Patterns)
        {
            var stripped = CharClass.Replace(pattern, "");
            var segments = StructuralGlue.Split(stripped);
            foreach (var seg in segments)
            {
                var lit = LiteralRun.Match(seg);
                if (lit.Success)
                {
                    r.Errors.Add(new GateFinding("G9", "LITERAL_PATTERN_REJECTED",
                        $"column '{col}' pattern contains a literal run '{lit.Value}' — use structural regex only"));
                    break;
                }
            }
        }
        return r;
    }
}
