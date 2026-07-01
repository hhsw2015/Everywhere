using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Everywhere.Mcp.OpenCli.Observation;

/// <summary>
/// SPEC §Phase 1 extraction rulebook. Persisted at
/// <c>~/.everywhere/extraction-rules.json</c> as an ordered array —
/// first URL-regex match wins. Ported (concept) from BAI extractionRules.
/// </summary>
public sealed class ExtractionRules
{
    public sealed class Rule
    {
        [JsonPropertyName("url_pattern")] public string UrlPattern { get; init; } = "";
        [JsonPropertyName("kind")] public string Kind { get; init; } = "css"; // css | xpath
        [JsonPropertyName("selector")] public string Selector { get; init; } = "";
        [JsonPropertyName("priority")] public int Priority { get; init; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    public ExtractionRules() : this(EverywherePaths.ExtractionRulesPath()) { }

    public ExtractionRules(string persistencePath)
    {
        _path = persistencePath;
    }

    public List<Rule> Load()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            using var stream = File.OpenRead(_path);
            var list = JsonSerializer.Deserialize<List<Rule>>(stream, JsonOpts) ?? [];
            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void Save(IEnumerable<Rule> rules)
    {
        var ordered = rules.OrderByDescending(r => r.Priority).ThenBy(r => r.UrlPattern).ToList();
        var tmp = _path + ".tmp";
        using (var stream = File.Create(tmp))
            JsonSerializer.Serialize(stream, ordered, JsonOpts);
        File.Move(tmp, _path, overwrite: true);
    }

    public Rule? Match(string url)
    {
        foreach (var r in Load())
        {
            if (Regex.IsMatch(url, r.UrlPattern, RegexOptions.IgnoreCase)) return r;
        }
        return null;
    }

    public void Upsert(Rule rule)
    {
        var all = Load();
        all.RemoveAll(x => x.UrlPattern == rule.UrlPattern && x.Kind == rule.Kind);
        all.Add(rule);
        Save(all);
    }
}
