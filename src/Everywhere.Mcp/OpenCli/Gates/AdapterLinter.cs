using Everywhere.Mcp.OpenCli.Memory;

namespace Everywhere.Mcp.OpenCli.Gates;

/// <summary>
/// SPEC §Phase 4 — orchestrator running all applicable gates in the
/// order the matrix declares. Callers pass any subset: adapter source
/// alone runs G3-G6/G8; add StrategyNote for G7; add VerifyFixture for G9.
/// </summary>
public sealed class AdapterLinter
{
    public GateResult Lint(string source, StrategyNote? strategyNote = null, VerifyFixture? fixture = null)
    {
        var result = GateResult.Empty();
        Merge(result, SignatureGuard.Check(source));
        Merge(result, TypedErrorLint.Check(source));
        Merge(result, SilentFallbackLint.Check(source));
        Merge(result, ClampLint.Check(source));
        Merge(result, MutationGuard.Check(strategyNote, source));
        Merge(result, LocaleAudit.Check(source));
        if (fixture is not null) Merge(result, VerifyFixtureGate.Check(fixture));
        return result;
    }

    private static void Merge(GateResult into, GateResult from)
    {
        into.Errors.AddRange(from.Errors);
        into.Warnings.AddRange(from.Warnings);
    }
}
