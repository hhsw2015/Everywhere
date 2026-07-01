using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Everywhere.Mcp.OpenCli.Observation;

namespace Everywhere.Mcp.OpenCli.Memory;

/// <summary>
/// SPEC §Phase 3 — per-site memory rooted at
/// <c>~/.everywhere/sites/&lt;domain&gt;/</c>. All paths validated via
/// <see cref="ResolveSitePath"/>: domain regex plus a canonical-path
/// check that the resolved path stays under <c>sites/</c>.
/// </summary>
public sealed class MemoryStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IClock _clock;
    private readonly Freshness _freshness;

    public MemoryStore(IClock? clock = null)
    {
        _clock = clock ?? SystemClock.Instance;
        _freshness = new Freshness(_clock);
    }

    /// <summary>Site dir under <c>~/.everywhere/sites/&lt;domain&gt;/</c>. Traversal-safe.</summary>
    public string ResolveSitePath(string domain, params string[] sub)
    {
        Identifier.Require("site", domain);
        foreach (var s in sub)
        {
            if (s is null) continue;
            // Sub-parts must be identifier-safe filenames — no path separators, no ".."
            if (s.Contains('/') || s.Contains('\\') || s.Contains(".."))
                throw new PathTraversalException(string.Join("/", sub), s);
        }
        var basePath = EverywherePaths.SitesDir();
        var candidate = Path.GetFullPath(Path.Combine(basePath, domain, Path.Combine(sub)));
        var canonicalBase = Path.GetFullPath(basePath);
        if (!candidate.StartsWith(canonicalBase + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && candidate != canonicalBase)
        {
            throw new PathTraversalException(string.Join("/", sub), candidate);
        }
        return candidate;
    }

    // ------------------- endpoints -----------------

    public EndpointSpec? ReadEndpoint(string domain, string name)
    {
        Identifier.Require("name", name);
        var path = ResolveSitePath(domain, "endpoints.json");
        var all = ReadDict<EndpointSpec>(path);
        return all.GetValueOrDefault(name);
    }

    public void WriteEndpoint(string domain, string name, EndpointSpec spec, bool force = false)
    {
        Identifier.Require("name", name);
        var path = ResolveSitePath(domain, "endpoints.json");
        MergeSafeWriter.MergeAtomic(path, () =>
        {
            var all = ReadDict<EndpointSpec>(path);
            if (!force && all.ContainsKey(name))
                throw new MergeConflictException(path, Sha256(JsonSerializer.Serialize(all[name], Json)));
            all[name] = spec;
            return JsonSerializer.Serialize(all, Json);
        });
        UpdateMetadata(domain, m => m.VerifiedAt = _clock.NowMs());
    }

    // ------------------- field maps -----------------

    public Dictionary<string, FieldMapEntry> ReadFieldMap(string domain)
    {
        var path = ResolveSitePath(domain, "field-map.json");
        return ReadDict<FieldMapEntry>(path);
    }

    public void WriteFieldMap(string domain, Dictionary<string, FieldMapEntry> mapping, bool force = false)
    {
        var path = ResolveSitePath(domain, "field-map.json");
        var existing = ReadDict<FieldMapEntry>(path);
        if (!force)
        {
            foreach (var key in mapping.Keys)
                if (existing.ContainsKey(key))
                    throw new MergeConflictException(path, Sha256(JsonSerializer.Serialize(existing[key], Json)));
        }
        foreach (var kv in mapping) existing[kv.Key] = kv.Value;
        MergeSafeWriter.WriteAtomic(path, JsonSerializer.Serialize(existing, Json));
    }

    // ------------------- strategy notes -----------------

    public StrategyNote? ReadStrategyNote(string domain, string name)
    {
        Identifier.Require("name", name);
        var path = ResolveSitePath(domain, "strategy-notes", name + ".md");
        if (!File.Exists(path)) return null;
        return StrategyNoteMarkdown.Parse(File.ReadAllText(path));
    }

    public string WriteStrategyNote(string domain, string name, StrategyNote note)
    {
        Identifier.Require("name", name);
        var dir = ResolveSitePath(domain, "strategy-notes");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".md");
        if (note.CreatedAt == 0)
        {
            note = new StrategyNote
            {
                Strategy = note.Strategy, Contract = note.Contract, Mutation = note.Mutation,
                Evidence = note.Evidence, Replay = note.Replay, CreatedAt = _clock.NowMs(),
            };
        }
        var body = StrategyNoteMarkdown.Serialize(note);
        MergeSafeWriter.WriteAtomic(path, body);
        return path;
    }

    // ------------------- verify fixtures -----------------

    public VerifyFixture? ReadVerifyFixture(string domain, string cmd)
    {
        Identifier.Require("cmd", cmd);
        var path = ResolveSitePath(domain, "verify", cmd + ".json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<VerifyFixture>(File.ReadAllText(path), Json);
    }

    public void WriteVerifyFixture(string domain, string cmd, VerifyFixture fixture, bool force = false)
    {
        Identifier.Require("cmd", cmd);
        var dir = ResolveSitePath(domain, "verify");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, cmd + ".json");
        if (!force && File.Exists(path))
            throw new MergeConflictException(path, Sha256(File.ReadAllText(path)));
        MergeSafeWriter.WriteAtomic(path, JsonSerializer.Serialize(fixture, Json));
    }

    // ------------------- notes -----------------

    public void AppendNote(string domain, string text)
    {
        var path = ResolveSitePath(domain, "notes.md");
        var iso = DateTimeOffset.FromUnixTimeMilliseconds(_clock.NowMs()).ToString("O");
        var line = $"\n\n---\n{iso}\n{text}";
        if (File.Exists(path))
        {
            File.AppendAllText(path, line);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, line.TrimStart());
        }
    }

    // ------------------- metadata / freshness -----------------

    public SiteMetadata ReadMetadata(string domain)
    {
        var path = ResolveSitePath(domain, "metadata.json");
        if (!File.Exists(path)) return new SiteMetadata();
        return JsonSerializer.Deserialize<SiteMetadata>(File.ReadAllText(path), Json) ?? new SiteMetadata();
    }

    public void UpdateMetadata(string domain, Action<SiteMetadata> mutate)
    {
        var path = ResolveSitePath(domain, "metadata.json");
        var m = ReadMetadata(domain);
        mutate(m);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        MergeSafeWriter.WriteAtomic(path, JsonSerializer.Serialize(m, Json));
    }

    public string Freshness(string domain)
    {
        var m = ReadMetadata(domain);
        if (m.VerifiedAt == 0) return "cold";
        return _freshness.Classify(m.VerifiedAt);
    }

    // ------------------- fixture rotator -----------------

    public string WriteSnapshot(string domain, string cmd, string content)
    {
        Identifier.Require("cmd", cmd);
        var dir = ResolveSitePath(domain, "fixtures");
        Directory.CreateDirectory(dir);
        var iso = DateTimeOffset.FromUnixTimeMilliseconds(_clock.NowMs()).ToString("yyyyMMddTHHmmssZ");
        var path = Path.Combine(dir, $"{cmd}-{iso}.json");
        File.WriteAllText(path, content);
        // Rotate — keep only last 5 per cmd
        var kept = new DirectoryInfo(dir)
            .EnumerateFiles($"{cmd}-*.json")
            .OrderByDescending(f => f.Name)
            .ToList();
        foreach (var old in kept.Skip(5))
        {
            try { old.Delete(); } catch { }
        }
        return path;
    }

    // ------------------- helpers -----------------

    private static Dictionary<string, T> ReadDict<T>(string path) where T : class
    {
        if (!File.Exists(path)) return new Dictionary<string, T>(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, T>>(File.ReadAllText(path), Json)
                ?? new Dictionary<string, T>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, T>(StringComparer.Ordinal);
        }
    }

    private static string Sha256(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

internal static class StrategyNoteMarkdown
{
    // Structured markdown per spec §Phase 3. Frontmatter (YAML-ish) +
    // freeform body. We serialize as JSON frontmatter (still parseable
    // markdown, and unambiguous) with the schema fields.
    public static string Serialize(StrategyNote note)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("strategy: ").Append(FlattenLine(note.Strategy)).Append('\n');
        sb.Append("contract: ").Append(FlattenLine(note.Contract)).Append('\n');
        sb.Append("mutation: ").Append(note.Mutation ? "true" : "false").Append('\n');
        sb.Append("created_at: ").Append(note.CreatedAt).Append('\n');
        sb.Append("---\n\n");
        sb.Append("## Evidence\n");
        foreach (var e in note.Evidence) sb.Append("- ").Append(FlattenLine(e)).Append('\n');
        sb.Append("\n## Replay\n").Append(note.Replay?.Replace("\r\n", "\n") ?? "").Append('\n');
        return sb.ToString();
    }

    private static string FlattenLine(string? s)
        => (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

    public static StrategyNote? Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var lines = body.Split('\n');
        if (lines.Length < 3 || lines[0].Trim() != "---") return null;
        var i = 1;
        var strategy = "public"; var contract = "stable"; var mutation = false; long createdAt = 0;
        for (; i < lines.Length && lines[i].Trim() != "---"; i++)
        {
            var line = lines[i];
            var eq = line.IndexOf(':');
            if (eq < 0) continue;
            var k = line[..eq].Trim();
            var v = line[(eq + 1)..].Trim();
            switch (k)
            {
                case "strategy": strategy = v; break;
                case "contract": contract = v; break;
                case "mutation": mutation = v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                case "created_at": long.TryParse(v, out createdAt); break;
            }
        }
        if (i >= lines.Length) return null;
        i++; // skip closing ---
        var evidence = new List<string>();
        var replay = new StringBuilder();
        var inReplay = false;
        for (; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("## Evidence")) { inReplay = false; continue; }
            if (line.StartsWith("## Replay")) { inReplay = true; continue; }
            if (inReplay)
            {
                if (replay.Length > 0) replay.Append('\n');
                replay.Append(line);
            }
            else if (line.StartsWith("- "))
            {
                evidence.Add(line[2..].Trim());
            }
        }
        return new StrategyNote
        {
            Strategy = strategy,
            Contract = contract,
            Mutation = mutation,
            CreatedAt = createdAt,
            Evidence = evidence,
            Replay = replay.ToString().TrimEnd(),
        };
    }
}
