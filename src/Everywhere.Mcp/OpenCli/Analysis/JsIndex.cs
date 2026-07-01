using System.Text.RegularExpressions;
using Everywhere.Mcp.OpenCli.Observation;

namespace Everywhere.Mcp.OpenCli.Analysis;

/// <summary>
/// SPEC §Phase 2 — line-indexed JS content store, keyed by URL.
/// Ported concept from BAI jsSourceIndex. Every stored body is passed
/// through the Redactor before insertion.
/// </summary>
public sealed class JsIndex
{
    public sealed record Entry(string Content, int[] LineStarts, string RedactedContent);
    public sealed record Hit(string Url, int Line, int Col, string Snippet);

    private readonly Dictionary<string, Entry> _byUrl = new(StringComparer.Ordinal);

    public void AddFromSession(CaptureSession session)
    {
        foreach (var req in session.Network.Requests)
        {
            if (!req.ResponseContentType.Contains("javascript", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!session.Network.BodiesByHash.TryGetValue(req.ResponseBodySha256, out var body)) continue;
            Add(req.Url, body);
        }
    }

    public void Add(string url, string body)
    {
        var redacted = Redactor.Body(body);
        var starts = ComputeLineStarts(redacted);
        _byUrl[url] = new Entry(body, starts, redacted);
    }

    public IReadOnlyDictionary<string, Entry> Entries => _byUrl;

    public List<Hit> Search(string pattern, int topK = 20)
    {
        // 200 ms cap defeats catastrophic backtracking without importing NonBacktracking
        // (some patterns aren't compatible with it).
        var rx = new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
        var hits = new List<Hit>();
        foreach (var (url, entry) in _byUrl)
        {
            // MatchCollection is lazy — timeout can fire during enumeration,
            // not during Matches() call. Catch inside the loop.
            try
            {
                foreach (Match m in rx.Matches(entry.RedactedContent))
                {
                    if (hits.Count >= topK) break;
                    var (line, col) = OffsetToLineCol(entry.LineStarts, m.Index);
                    var start = Math.Max(0, m.Index - 100);
                    var end = Math.Min(entry.RedactedContent.Length, m.Index + m.Length + 100);
                    hits.Add(new Hit(url, line, col, entry.RedactedContent[start..end]));
                }
            }
            catch (RegexMatchTimeoutException) { continue; }
            if (hits.Count >= topK) break;
        }
        return hits;
    }

    private static int[] ComputeLineStarts(string s)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < s.Length; i++) if (s[i] == '\n') starts.Add(i + 1);
        return starts.ToArray();
    }

    private static (int line, int col) OffsetToLineCol(int[] starts, int offset)
    {
        var lo = 0; var hi = starts.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (starts[mid] <= offset) lo = mid;
            else hi = mid - 1;
        }
        return (lo + 1, offset - starts[lo] + 1);
    }
}
