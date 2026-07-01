using System.Text.RegularExpressions;

namespace Everywhere.Mcp.OpenCli.Gates;

/// <summary>
/// SPEC §Phase 4 G4 — every <c>throw</c> must be a <c>new &lt;TypedErrorClass&gt;(...)</c>.
/// <c>new Error(...)</c> / <c>new CliError('STRING')</c> → fail
/// UNTYPED_THROW with the line.
///
/// SPEC-approved typed errors (§3.2 error hierarchy):
///   ArgumentError | AuthRequiredError | CommandExecutionError
///   ConfigError | EmptyResultError | TimeoutError
/// </summary>
public static class TypedErrorLint
{
    private static readonly HashSet<string> ApprovedThrows = new(StringComparer.Ordinal)
    {
        "ArgumentError", "AuthRequiredError", "CommandExecutionError",
        "ConfigError", "EmptyResultError", "TimeoutError",
    };

    // Multiline mode + \s+ crosses line breaks, catching JS ASI form
    // `throw\n  new EmptyResultError(...)`.
    private static readonly Regex ThrowStatement = new(
        @"throw\s+(new\s+([A-Za-z_][A-Za-z0-9_]*)|[^;\n]*)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static GateResult Check(string source)
    {
        var r = GateResult.Empty();
        var stripped = AdapterSourceScan.StripCommentsAndStrings(source);

        var linePositions = new List<int> { 0 };
        for (int i = 0; i < stripped.Length; i++) if (stripped[i] == '\n') linePositions.Add(i + 1);
        int LineOf(int idx)
        {
            var lo = 0; var hi = linePositions.Count - 1;
            while (lo < hi) { var mid = (lo + hi + 1) / 2; if (linePositions[mid] <= idx) lo = mid; else hi = mid - 1; }
            return lo + 1;
        }

        foreach (Match m in ThrowStatement.Matches(stripped))
        {
            var line = LineOf(m.Index);
            var newMatch = m.Groups[2];
            if (newMatch.Success)
            {
                var cls = newMatch.Value;
                if (!ApprovedThrows.Contains(cls))
                {
                    r.Errors.Add(new GateFinding("G4", "UNTYPED_THROW",
                        $"throw new {cls}(...) is not in the approved typed-error set",
                        line, m.Value.Trim()));
                }
                continue;
            }
            r.Errors.Add(new GateFinding("G4", "UNTYPED_THROW",
                "throw <expression> — only 'throw new <TypedError>(...)' is allowed",
                line, m.Value.Trim()));
        }
        return r;
    }
}
