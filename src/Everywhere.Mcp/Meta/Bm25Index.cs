namespace Everywhere.Mcp.Meta;

/// <summary>
/// SPEC §Phase 6 — inline BM25 (~80 LOC). Tokenize on <c>[^a-z0-9]+</c>
/// after lowercase; <c>k1=1.5, b=0.75</c>. Index over (tool_name, description).
/// No Lucene dependency.
/// </summary>
public sealed class Bm25Index
{
    private const double K1 = 1.5;
    private const double B = 0.75;

    public sealed record Doc(string Name, string Description);
    public sealed record Hit(string Name, string Description, double Score);

    private readonly List<Doc> _docs = new();
    private readonly Dictionary<string, Dictionary<int, int>> _postings = new(StringComparer.Ordinal);
    private double _avgLen;
    private readonly List<int> _lens = new();

    public void Add(Doc doc)
    {
        var idx = _docs.Count;
        _docs.Add(doc);
        var tokens = Tokenize(doc.Name + " " + doc.Description);
        _lens.Add(tokens.Count);
        foreach (var t in tokens)
        {
            if (!_postings.TryGetValue(t, out var post))
                _postings[t] = post = new Dictionary<int, int>();
            post[idx] = post.GetValueOrDefault(idx) + 1;
        }
        _avgLen = _lens.Count == 0 ? 0 : _lens.Average();
    }

    public List<Hit> Search(string query, int topK = 5)
    {
        if (_docs.Count == 0) return [];
        var q = Tokenize(query);
        var scores = new Dictionary<int, double>();
        foreach (var t in q)
        {
            if (!_postings.TryGetValue(t, out var post)) continue;
            var idf = Math.Log(1.0 + (_docs.Count - post.Count + 0.5) / (post.Count + 0.5));
            foreach (var (docIdx, tf) in post)
            {
                var len = _lens[docIdx];
                var norm = 1 - B + B * (len / Math.Max(1.0, _avgLen));
                var contribution = idf * ((tf * (K1 + 1)) / (tf + K1 * norm));
                scores[docIdx] = scores.GetValueOrDefault(docIdx) + contribution;
            }
        }
        return scores
            .OrderByDescending(kv => kv.Value)
            .Take(topK)
            .Select(kv => new Hit(_docs[kv.Key].Name, _docs[kv.Key].Description, kv.Value))
            .ToList();
    }

    private static List<string> Tokenize(string s)
    {
        var res = new List<string>();
        var sb = new System.Text.StringBuilder();
        foreach (var c in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0) { res.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) res.Add(sb.ToString());
        return res;
    }
}
