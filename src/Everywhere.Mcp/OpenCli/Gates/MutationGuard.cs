using System.Text.RegularExpressions;
using Everywhere.Mcp.OpenCli.Memory;

namespace Everywhere.Mcp.OpenCli.Gates;

/// <summary>
/// SPEC §Phase 4 G7 / §2.6 — POST/PUT/DELETE/PATCH endpoints require
/// <c>strategy_note.mutation:true</c>. Two checks:
/// 1. Strategy-note evidence lines matching /\b(POST|PUT|DELETE|PATCH)\b/i
///    → fail hard if <c>mutation !== true</c>.
/// 2. AST-side: fetch method or page.evaluate template with mutating verb
///    → warn (advisory).
/// </summary>
public static class MutationGuard
{
    private static readonly Regex MutationVerb = new(
        @"\b(POST|PUT|DELETE|PATCH)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static GateResult Check(StrategyNote? note, string adapterSource)
    {
        var r = GateResult.Empty();
        var evidenceMentionsMutation = note?.Evidence.Any(e => e is not null && MutationVerb.IsMatch(e)) == true;
        var codeMentionsMutation = AdapterSourceScan.HasMutationCall(adapterSource);
        var declaredMutation = note?.Mutation == true;

        if (evidenceMentionsMutation && !declaredMutation)
        {
            r.Errors.Add(new GateFinding("G7", "MUTATION_UNAPPROVED",
                "strategy-note evidence names a mutating verb (POST/PUT/DELETE/PATCH) but mutation:false"));
            return r;
        }
        if (codeMentionsMutation && !declaredMutation)
        {
            // Promote to hard error when a strategy note is present and
            // explicitly says mutation:false — the source contradicts the
            // approved contract, adapter_save must refuse.
            if (note is not null)
            {
                r.Errors.Add(new GateFinding("G7", "MUTATION_UNAPPROVED",
                    "adapter source contains a mutating fetch/XHR method but strategy-note mutation:false"));
            }
            else
            {
                // Note absent — advisory only; adapter_lint standalone path.
                r.Warnings.Add(new GateFinding("G7", "MUTATION_UNAPPROVED",
                    "adapter source contains a mutating method — declare strategy-note mutation:true or remove"));
            }
        }
        return r;
    }
}
