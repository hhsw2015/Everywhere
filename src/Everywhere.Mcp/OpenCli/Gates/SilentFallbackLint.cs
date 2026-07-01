using System.Text.RegularExpressions;

namespace Everywhere.Mcp.OpenCli.Gates;

/// <summary>
/// SPEC §Phase 4 G5 — <c>return []</c> at fn end without a prior throw
/// is a silent fallback and must fail (<c>throw new EmptyResultError</c>
/// is the approved form). Sentinel rows (arrays of objects with only
/// empty/placeholder values) also fail.
/// </summary>
public static class SilentFallbackLint
{
    private static readonly Regex ReturnEmptyArray = new(
        @"(?<![.\w])return\s*(" +
            @"\[\s*\]" +                     // return []
            @"|Array\.of\s*\(\s*\)" +        // return Array.of()
            @"|new\s+Array\s*\(\s*0\s*\)" +  // return new Array(0)
            @"|\[\s*\.\.\.\s*\[\s*\]\s*\]" + // return [...[]]
        @")\s*;?",
        RegexOptions.Compiled);

    private static readonly Regex SentinelRow = new(
        @"return\s*\[\s*\{[^{}]*['""](-|N\/A|--|)['""][^{}]*\}\s*(,\s*\{[^{}]*['""](-|N\/A|--|)['""][^{}]*\})*\s*\]",
        RegexOptions.Compiled);

    public static GateResult Check(string source)
    {
        var r = GateResult.Empty();
        var stripped = AdapterSourceScan.StripCommentsAndStrings(source);

        foreach (var (line, text) in AdapterSourceScan.Lines(stripped))
        {
            if (ReturnEmptyArray.IsMatch(text))
            {
                r.Errors.Add(new GateFinding("G5", "SILENT_FALLBACK_RETURN_EMPTY",
                    "return [] hides empty results — throw new EmptyResultError() instead",
                    line, text.Trim()));
            }
        }
        // Keep the original source for sentinel-row detection (string
        // literals matter here).
        var sentinelMatch = SentinelRow.Match(source);
        if (sentinelMatch.Success)
        {
            var lineNo = source[..sentinelMatch.Index].Count(c => c == '\n') + 1;
            r.Errors.Add(new GateFinding("G5", "SENTINEL_ROW",
                "row placeholder values ('-'/'N/A'/empty) leak to callers — throw EmptyResultError",
                lineNo, sentinelMatch.Value));
        }
        return r;
    }
}
