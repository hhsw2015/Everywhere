using System.Text.RegularExpressions;

namespace Everywhere.DocReaders.Tests;

public static class Similarity
{
    private static readonly Regex TokenRx = new(@"\w+", RegexOptions.Compiled);

    public static double NormalizedTokenJaccard(string a, string b)
    {
        var tokA = Tokenize(a);
        var tokB = Tokenize(b);
        if (tokA.Count == 0 && tokB.Count == 0) return 1.0;
        var intersect = new HashSet<string>(tokA);
        intersect.IntersectWith(tokB);
        var union = new HashSet<string>(tokA);
        union.UnionWith(tokB);
        return union.Count == 0 ? 0.0 : (double)intersect.Count / union.Count;
    }

    private static HashSet<string> Tokenize(string s) =>
        TokenRx.Matches(s).Select(m => m.Value.ToLowerInvariant()).ToHashSet();

    public static string Diff(string actual, string golden, int maxLen)
    {
        var a = actual.Length > maxLen ? actual[..maxLen] + "..." : actual;
        var g = golden.Length > maxLen ? golden[..maxLen] + "..." : golden;
        return $"--- actual ({actual.Length} chars) ---\n{a}\n--- golden ({golden.Length} chars) ---\n{g}";
    }
}
