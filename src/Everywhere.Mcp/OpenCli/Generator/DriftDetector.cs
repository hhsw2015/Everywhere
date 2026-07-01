using Everywhere.Mcp.OpenCli.Memory;

namespace Everywhere.Mcp.OpenCli.Generator;

/// <summary>
/// SPEC §Phase 5 drift detector — rerun adapter, hash output, compare
/// to <c>meta.last_success_hash</c>. Identical → ok; ≥3 pattern matches
/// against verify fixture → drift; else broken. Never auto-regens.
/// </summary>
public sealed record DriftReport(string Status, string? Diff, long CheckedAt);

public static class DriftDetector
{
    public static DriftReport Compare(string currentOutput, VerifyFixture fixture, string? lastSuccessHash, long checkedAt)
    {
        var currentHash = LocalRegistry.Sha256Of(currentOutput);
        if (!string.IsNullOrEmpty(lastSuccessHash) && lastSuccessHash == currentHash)
            return new DriftReport("ok", null, checkedAt);

        var matches = 0;
        foreach (var (col, pattern) in fixture.Patterns)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(currentOutput, pattern))
                matches++;
        }
        var status = matches >= 3 ? "drift" : "broken";
        return new DriftReport(status, $"pattern_matches={matches}/{fixture.Patterns.Count}", checkedAt);
    }
}
