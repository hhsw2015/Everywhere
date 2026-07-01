using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenCli.Analysis;
using Everywhere.Mcp.OpenCli.Memory;
using Everywhere.Mcp.OpenCli.Observation;

namespace Everywhere.Mcp.OpenCli.Generator;

/// <summary>
/// SPEC §Phase 5 scaffold — template + verdict endpoints + strategy note
/// + neighbor. Renders both the JS skeleton and the LLM prompt with
/// every <c>{{...}}</c> variable inlined (acceptance E9).
/// </summary>
public sealed record ScaffoldRequest(
    string Site, string Name, string SessionId,
    string StrategyNotePath, StrategyNote StrategyNote,
    NeighborMatch? Neighbor, string NeighborSource, string NeighborPath,
    List<VerdictOutcome> LikelyEndpoints,
    Dictionary<string, string> FieldMapHints,
    string Description);

public sealed record ScaffoldResult(
    string SkeletonSource, string NeighborSource, string NeighborPath,
    bool NeighborHintWeak, string LlmPrompt,
    List<VerdictOutcome> VerdictEndpoints,
    StrategyNote StrategyNote,
    Dictionary<string, string> FieldMapHints);

public static class Scaffold
{
    // SPEC verbatim skeleton (§Phase 5). {{...}} substituted below.
    private const string Skeleton = @"// AUTO-GENERATED skeleton for {{site}}/{{name}}
// Strategy: {{strategy}} | Contract: {{contract}}
// Capture session: {{session_id}}
// Neighbor reference: {{neighbor_site}}/{{neighbor_name}}
import { cli, Strategy } from '@jackwener/opencli/registry';
import { ArgumentError, AuthRequiredError, CommandExecutionError, EmptyResultError, TimeoutError } from '@jackwener/opencli/errors';

cli({
  site: '{{site}}',
  name: '{{name}}',
  description: '{{description}}',
  domain: '{{domain}}',
  strategy: Strategy.{{STRATEGY_UPPER}},
  browser: {{browser_bool}},
  navigateBefore: {{navigate_before_or_false}},
  args: [ {{args_json_lines}} ],
  columns: [{{columns_quoted}}],
  {{func_signature}}: async ({{func_params}}) => {
    // TODO-1: fetch {{endpoint_1_method}} {{endpoint_1_url}}
    //   Verdict: likely_data (score {{endpoint_1_score}})
    //   Signature: {{signature_scheme}} | Field-map hints: {{field_map_summary}}
    // TODO-2: parse to rows matching columns
    // TODO-3: throw typed error on non-200/empty/auth-fail
    throw new CommandExecutionError('adapter body not implemented');
  },
});
";

    public static ScaffoldResult Render(ScaffoldRequest req)
    {
        var strategy = req.StrategyNote.Strategy;
        var browser = strategy is "cookie" or "intercept" or "ui";
        var contract = req.StrategyNote.Contract;
        var firstEndpoint = req.LikelyEndpoints.FirstOrDefault();
        var neighborHintWeak = req.Neighbor is null || req.Neighbor.Score <= 0;
        var neighborSite = req.Neighbor?.Hint.Site ?? "(none)";
        var neighborName = req.Neighbor?.Hint.Name ?? "(none)";
        var domain = req.Neighbor?.Hint.Domain ?? "";

        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["site"] = req.Site,
            ["name"] = req.Name,
            ["description"] = req.Description,
            ["session_id"] = req.SessionId,
            ["strategy"] = strategy,
            ["STRATEGY_UPPER"] = strategy.ToUpperInvariant(),
            ["contract"] = contract,
            ["browser_bool"] = browser ? "true" : "false",
            ["navigate_before_or_false"] = "false",
            ["neighbor_site"] = neighborSite,
            ["neighbor_name"] = neighborName,
            ["domain"] = domain,
            ["args_json_lines"] = "",
            ["columns_quoted"] = "",
            ["func_signature"] = "func",
            ["func_params"] = browser ? "page, args" : "args",
            ["endpoint_1_method"] = firstEndpoint is null ? "<TBD>" : GetEndpointMethod(req, firstEndpoint),
            ["endpoint_1_url"] = firstEndpoint is null ? "<TBD>" : GetEndpointUrl(req, firstEndpoint),
            ["endpoint_1_score"] = firstEndpoint is null ? "0" : firstEndpoint.RealDataScore.ToString(),
            ["signature_scheme"] = req.FieldMapHints.GetValueOrDefault("signature_scheme") ?? "unknown",
            ["field_map_summary"] = SummarizeFieldMap(req.FieldMapHints),
        };

        var skeleton = Substitute(Skeleton, vars);
        var prompt = RenderPrompt(req, vars);

        return new ScaffoldResult(
            SkeletonSource: skeleton,
            NeighborSource: req.NeighborSource,
            NeighborPath: req.NeighborPath,
            NeighborHintWeak: neighborHintWeak,
            LlmPrompt: prompt,
            VerdictEndpoints: req.LikelyEndpoints,
            StrategyNote: req.StrategyNote,
            FieldMapHints: req.FieldMapHints);
    }

    private static string Substitute(string template, Dictionary<string, string> vars)
    {
        var sb = new StringBuilder(template);
        foreach (var kv in vars)
            sb.Replace("{{" + kv.Key + "}}", kv.Value);
        return sb.ToString();
    }

    private static string RenderPrompt(ScaffoldRequest req, Dictionary<string, string> vars)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Adapter body generation prompt");
        sb.AppendLine();
        sb.AppendLine($"Site: {req.Site}");
        sb.AppendLine($"Name: {req.Name}");
        sb.AppendLine($"Description: {req.Description}");
        sb.AppendLine($"Strategy: {req.StrategyNote.Strategy}");
        sb.AppendLine($"Contract: {req.StrategyNote.Contract}");
        sb.AppendLine($"Mutation approved: {(req.StrategyNote.Mutation ? "yes" : "no")}");
        sb.AppendLine();
        sb.AppendLine("## Skeleton (fill TODO blocks; keep everything else byte-identical)");
        sb.AppendLine("```javascript");
        sb.AppendLine(Substitute(Skeleton, vars));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Verdict endpoints (likely_data / maybe_data)");
        foreach (var ep in req.LikelyEndpoints.Take(5))
        {
            sb.AppendLine($"- {GetEndpointMethod(req, ep)} {GetEndpointUrl(req, ep)} (score {ep.RealDataScore}, verdict {ep.Verdict})");
            foreach (var (path, type) in ep.ResponseShape.Take(10))
                sb.AppendLine($"  - {path}: {type}");
        }
        sb.AppendLine();
        sb.AppendLine("## Strategy note evidence");
        foreach (var e in req.StrategyNote.Evidence)
            sb.AppendLine("- " + e.Replace('\n', ' '));
        sb.AppendLine();
        sb.AppendLine("## Replay recipe");
        sb.AppendLine(req.StrategyNote.Replay);
        sb.AppendLine();
        sb.AppendLine("## Neighbor reference");
        sb.AppendLine($"Path: {req.NeighborPath}");
        if (req.Neighbor is null || req.Neighbor.Score <= 0)
            sb.AppendLine("(no strong neighbor — apply general OpenCLI patterns from the SKILL runbook)");
        else
            sb.AppendLine($"Score: {req.Neighbor.Score:F1} ({req.Neighbor.Reason})");
        sb.AppendLine();
        sb.AppendLine("## Field-map hints");
        foreach (var (k, v) in req.FieldMapHints) sb.AppendLine($"- {k}: {v}");
        sb.AppendLine();
        sb.AppendLine("## Output contract");
        sb.AppendLine("- Return ONLY the JS module source, no fences.");
        sb.AppendLine("- Approved throws: ArgumentError / AuthRequiredError / CommandExecutionError / EmptyResultError / TimeoutError.");
        sb.AppendLine("- No `return []`; use `throw new EmptyResultError`.");
        sb.AppendLine("- No sentinel rows.");
        sb.AppendLine("- No clamping on args (use ArgumentError).");
        if (!req.StrategyNote.Mutation)
            sb.AppendLine("- Strategy note mutation=false — every declared endpoint MUST be GET.");
        return sb.ToString();
    }

    private static string GetEndpointMethod(ScaffoldRequest req, VerdictOutcome ep) => "GET";
    private static string GetEndpointUrl(ScaffoldRequest req, VerdictOutcome ep) => ep.RequestId; // request_id stands in when caller lacks URL context

    private static string SummarizeFieldMap(Dictionary<string, string> hints)
    {
        return hints.Count == 0 ? "(none)" : string.Join(", ", hints.Select(kv => $"{kv.Key}={kv.Value}"));
    }
}
