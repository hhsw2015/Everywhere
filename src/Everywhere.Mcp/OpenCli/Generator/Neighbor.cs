namespace Everywhere.Mcp.OpenCli.Generator;

/// <summary>
/// SPEC §Phase 5 neighbor search — Jaccard on description tokens plus a
/// small weighted-scoring shim. Score = 0 → LLM prompt notes weak neighbor.
/// </summary>
public sealed record NeighborHint(
    string Site, string Name, string Description, string Strategy, string? Domain,
    bool Browser, IReadOnlyList<string> Columns);

public sealed record NeighborMatch(NeighborHint Hint, double Score, string Reason);

public static class Neighbor
{
    public static List<NeighborMatch> Search(
        IEnumerable<NeighborHint> pool,
        string descriptionHint,
        string strategyHint,
        string? domainSuffix,
        bool browser,
        IReadOnlyList<string> columns,
        int top = 5)
    {
        var descTokens = Tokenize(descriptionHint).ToHashSet();
        var colSet = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
        var scored = new List<NeighborMatch>();
        foreach (var n in pool)
        {
            var nDesc = Tokenize(n.Description).ToHashSet();
            var jac = Jaccard(descTokens, nDesc) * 10;
            var stratMatch = n.Strategy == strategyHint ? 5 : 0;
            var domMatch = domainSuffix is not null && n.Domain is not null
                           && n.Domain.EndsWith(domainSuffix, StringComparison.OrdinalIgnoreCase) ? 3 : 0;
            var browserMatch = n.Browser == browser ? 2 : 0;
            var colIntersection = n.Columns.Count(c => colSet.Contains(c));
            var total = jac + stratMatch + domMatch + browserMatch + colIntersection;
            var reason = $"jac={jac:F1} strat={stratMatch} dom={domMatch} br={browserMatch} col={colIntersection}";
            scored.Add(new NeighborMatch(n, total, reason));
        }
        return scored.OrderByDescending(m => m.Score).Take(top).ToList();
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 0;
        var inter = a.Intersect(b).Count();
        var union = a.Union(b).Count();
        return union == 0 ? 0 : (double)inter / union;
    }

    private static IEnumerable<string> Tokenize(string s)
    {
        if (string.IsNullOrEmpty(s)) yield break;
        var sb = new System.Text.StringBuilder();
        foreach (var c in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}
