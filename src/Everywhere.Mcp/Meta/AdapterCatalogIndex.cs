using System.Text.Json;
using Everywhere.Mcp.OpenCli.Generator;

namespace Everywhere.Mcp.Meta;

/// <summary>
/// SPEC docs/specs/everywhere-self-expanding.md Phase 6.5 — BM25 index
/// over the merged catalog of vendored (`3rd/opencli/cli-manifest.json`)
/// + local (`~/.everywhere/adapters/&lt;site&gt;/`) adapters. Consumers
/// call <see cref="Search"/> to answer "find me an adapter that does X".
/// </summary>
public sealed class AdapterCatalogIndex
{
    public sealed record Entry(string Site, string Name, string Description, string Origin, string Strategy);
    public sealed record Hit(Entry Entry, double Score);

    private Bm25Index _bm25 = new();
    private readonly Dictionary<string, Entry> _byKey = new(StringComparer.Ordinal);

    /// <summary>Loaded lazily; refresh() rebuilds when local registry changed.</summary>
    public int Count => _byKey.Count;

    public void Load(string manifestPath)
    {
        _byKey.Clear();
        if (File.Exists(manifestPath))
        {
            try
            {
                using var stream = File.OpenRead(manifestPath);
                using var doc = JsonDocument.Parse(stream);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var site = el.TryGetProperty("site", out var s) ? s.GetString() ?? "" : "";
                    var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var desc = el.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                    var strategy = el.TryGetProperty("strategy", out var st) ? st.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(site) || string.IsNullOrEmpty(name)) continue;
                    Add(new Entry(site, name, desc, "vendored", strategy));
                }
            }
            catch (JsonException) { /* corrupt manifest — treat as empty */ }
        }
        // Merge in local adapters. We don't parse the .js source; the meta.json
        // has origin + version + timestamps but not description, so we fall back
        // to "<site>/<name> (local)" when no separate description exists.
        foreach (var (site, name) in LocalRegistry.List())
        {
            var meta = LocalRegistry.LoadMeta(site, name);
            var desc = $"local adapter {site}/{name} (v{meta?.AdapterVersion ?? 1})";
            // Do not overwrite a vendored entry with the same key — SPEC §2.1
            // says vendored wins on collision unless SHADOW=1.
            var key = site + "/" + name;
            if (_byKey.ContainsKey(key)) continue;
            Add(new Entry(site, name, desc, "local", ""));
        }
        RebuildBm25();
    }

    private void Add(Entry e)
    {
        var key = e.Site + "/" + e.Name;
        _byKey[key] = e;
    }

    private void RebuildBm25()
    {
        // Bm25Index is add-only; make a fresh instance on each Load.
        var replacement = new Bm25Index();
        foreach (var e in _byKey.Values)
            replacement.Add(new Bm25Index.Doc(e.Site + "/" + e.Name, $"{e.Site} {e.Name} {e.Description} {e.Strategy}"));
        _bm25 = replacement;
    }

    public List<Hit> Search(string query, int topK = 5)
    {
        var raw = _bm25.Search(query, topK * 2);
        var hits = new List<Hit>();
        foreach (var r in raw)
        {
            if (_byKey.TryGetValue(r.Name, out var e))
                hits.Add(new Hit(e, r.Score));
            if (hits.Count >= topK) break;
        }
        return hits;
    }
}
