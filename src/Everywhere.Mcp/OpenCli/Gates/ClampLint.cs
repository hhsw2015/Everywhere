using System.Text.RegularExpressions;

namespace Everywhere.Mcp.OpenCli.Gates;

/// <summary>
/// SPEC §Phase 4 G6 — Math.min/max on args, ternary clamps → fail.
/// Enforcement: use validation + <c>throw new ArgumentError</c>.
/// </summary>
public static class ClampLint
{
    // Math.min(N, args.X) or Math.min(args.X, N)
    private static readonly Regex MathClamp = new(
        @"Math\.(min|max)\s*\(\s*[^)]*\bargs\.[A-Za-z_][A-Za-z0-9_]*[^)]*\)",
        RegexOptions.Compiled);

    // (args.limit > N ? N : args.limit) — ternary clamps
    private static readonly Regex TernaryClamp = new(
        @"\bargs\.[A-Za-z_][A-Za-z0-9_]*\s*[<>]=?\s*\d+\s*\?\s*\d+\s*:\s*args\.[A-Za-z_][A-Za-z0-9_]*",
        RegexOptions.Compiled);

    public static GateResult Check(string source)
    {
        var r = GateResult.Empty();
        var stripped = AdapterSourceScan.StripCommentsAndStrings(source);
        foreach (var (line, text) in AdapterSourceScan.Lines(stripped))
        {
            if (MathClamp.IsMatch(text) || TernaryClamp.IsMatch(text))
            {
                r.Errors.Add(new GateFinding("G6", "EXTERNAL_ARG_CLAMPED",
                    "clamp on args.* value hides out-of-range input — validate + throw ArgumentError instead",
                    line, text.Trim()));
            }
        }
        return r;
    }
}
