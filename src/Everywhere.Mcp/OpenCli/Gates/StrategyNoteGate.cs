using Everywhere.Mcp.OpenCli.Memory;

namespace Everywhere.Mcp.OpenCli.Gates;

/// <summary>
/// SPEC §Phase 4 G1 / G2 — strategy note must exist (G1) and must be
/// complete (G2: ≥3 evidence, each ≥20 char, replay ≥50 char, valid enums).
/// </summary>
public sealed class StrategyNoteGate(MemoryStore store)
{
    private readonly MemoryStore _store = store;

    public GateResult Check(string site, string name)
    {
        var r = GateResult.Empty();
        var note = _store.ReadStrategyNote(site, name);
        if (note is null)
        {
            r.Errors.Add(new GateFinding("G1", "STRATEGY_NOTE_MISSING", $"no strategy note at sites/{site}/strategy-notes/{name}.md"));
            return r;
        }
        if (!note.IsComplete(out var missing))
        {
            r.Errors.Add(new GateFinding("G2", "STRATEGY_NOTE_INCOMPLETE",
                "strategy note missing/short: " + string.Join(", ", missing)));
        }
        return r;
    }
}
