using Everywhere.Mcp.OpenCli.Observation;

namespace Everywhere.Mcp.OpenCli.Memory;

/// <summary>SPEC §Phase 3 — fresh &lt;30d, stale 30-90d, cold &gt;90d.</summary>
public sealed class Freshness(IClock clock)
{
    private readonly IClock _clock = clock;
    private const long DayMs = 24L * 3600 * 1000;

    public string Classify(long verifiedAt)
    {
        var age = _clock.NowMs() - verifiedAt;
        if (age < 30 * DayMs) return "fresh";
        if (age < 90 * DayMs) return "stale";
        return "cold";
    }
}
