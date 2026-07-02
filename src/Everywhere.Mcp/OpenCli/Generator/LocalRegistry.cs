using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Everywhere.Mcp.OpenCli.Memory;
using Everywhere.Mcp.OpenCli.Observation;

namespace Everywhere.Mcp.OpenCli.Generator;

/// <summary>
/// SPEC §Phase 5 local adapter registry. Rooted at
/// <c>~/.everywhere/adapters/&lt;site&gt;/&lt;name&gt;.js</c>. Vendored
/// adapters always win unless <c>EVERYWHERE_MCP_LOCAL_SHADOW=1</c>.
/// </summary>
public sealed class GeneratorMeta
{
    [JsonPropertyName("generator_version")] public string GeneratorVersion { get; init; } = "0.1";
    [JsonPropertyName("generated_at")] public long GeneratedAt { get; init; }
    [JsonPropertyName("session_id")] public string SessionId { get; init; } = "";
    [JsonPropertyName("strategy_note_path")] public string StrategyNotePath { get; init; } = "";
    [JsonPropertyName("verify_fixture_path")] public string VerifyFixturePath { get; init; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
    [JsonPropertyName("origin")] public string Origin { get; init; } = "local";
    [JsonPropertyName("adapter_version")] public int AdapterVersion { get; set; } = 1;
    [JsonPropertyName("last_success_hash")] public string? LastSuccessHash { get; set; }
    [JsonPropertyName("last_success_at")] public long? LastSuccessAt { get; set; }
}

public static class LocalRegistry
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ResolvePath(string site, string name)
    {
        Identifier.Require("site", site);
        Identifier.Require("name", name);
        return Path.Combine(EverywherePaths.AdaptersDir(), site, name + ".js");
    }

    public static string ResolveMetaPath(string site, string name) => ResolvePath(site, name)[..^3] + ".meta.json";
    public static string ResolveVerifyPath(string site, string name) => ResolvePath(site, name)[..^3] + ".verify.json";

    public static bool Exists(string site, string name) => File.Exists(ResolvePath(site, name));

    public static string? LoadSource(string site, string name)
    {
        var p = ResolvePath(site, name);
        return File.Exists(p) ? File.ReadAllText(p) : null;
    }

    /// <summary>
    /// Metadata-only view of a local adapter. Used by resolver-style APIs that
    /// only need the identity + origin — <c>OpenCliRuntime.InvokeAsync</c>
    /// bypasses this and loads the JS module through its own V8 pipeline
    /// (see <c>EnsureLocalAdapterLoadedAsync</c>) so the returned
    /// <see cref="AdapterDef"/>'s <c>Func</c> is non-null at execution time.
    /// </summary>
    public static Task<AdapterDef?> LoadAsync(string site, string name, CancellationToken ct)
    {
        _ = ct;
        if (!File.Exists(ResolvePath(site, name))) return Task.FromResult<AdapterDef?>(null);
        var meta = LoadMeta(site, name);
        var def = new AdapterDef(
            site: site,
            name: name,
            description: meta is null ? $"local adapter {site}/{name}" : $"local adapter {site}/{name} (v{meta.AdapterVersion})",
            strategy: "public",
            browser: false,
            access: "read",
            domain: null,
            aliases: null,
            args: null,
            columns: null,
            func: null,
            pipeline: null,
            navigateBefore: null)
        {
            Origin = "local",
        };
        return Task.FromResult<AdapterDef?>(def);
    }

    public static GeneratorMeta? LoadMeta(string site, string name)
    {
        var p = ResolveMetaPath(site, name);
        if (!File.Exists(p)) return null;
        return JsonSerializer.Deserialize<GeneratorMeta>(File.ReadAllText(p), Json);
    }

    public static VerifyFixture? LoadVerify(string site, string name)
    {
        var p = ResolveVerifyPath(site, name);
        if (!File.Exists(p)) return null;
        return JsonSerializer.Deserialize<VerifyFixture>(File.ReadAllText(p), Json);
    }

    public static IEnumerable<(string Site, string Name)> List()
    {
        var root = EverywherePaths.AdaptersDir();
        if (!Directory.Exists(root)) yield break;
        foreach (var siteDir in Directory.EnumerateDirectories(root))
        {
            var site = Path.GetFileName(siteDir);
            foreach (var file in Directory.EnumerateFiles(siteDir, "*.js"))
            {
                yield return (site, Path.GetFileNameWithoutExtension(file));
            }
        }
    }

    public static string Save(string site, string name, string source, VerifyFixture fixture, GeneratorMeta meta)
    {
        var path = ResolvePath(site, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Backup previous version if present
        if (File.Exists(path))
        {
            // Suffix with ISO-timestamp so retries don't overwrite older
            // backups (F26). Regeneration on same day/second still collides
            // — accepted trade-off since we don't have a monotonic clock at
            // this layer.
            var prev = LoadMeta(site, name);
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ");
            var backup = Path.Combine(Path.GetDirectoryName(path)!, $"{name}.{prev?.AdapterVersion ?? meta.AdapterVersion - 1}.{stamp}.bak.js");
            try { File.Copy(path, backup, overwrite: true); } catch { }
        }
        MergeSafeWriter.WriteAtomic(path, source);
        var metaWithHash = new GeneratorMeta
        {
            GeneratorVersion = meta.GeneratorVersion,
            GeneratedAt = meta.GeneratedAt,
            SessionId = meta.SessionId,
            StrategyNotePath = meta.StrategyNotePath,
            VerifyFixturePath = meta.VerifyFixturePath,
            Sha256 = Sha256Of(source),
            Origin = "local",
            AdapterVersion = meta.AdapterVersion,
            LastSuccessHash = meta.LastSuccessHash,
            LastSuccessAt = meta.LastSuccessAt,
        };
        MergeSafeWriter.WriteAtomic(ResolveMetaPath(site, name), JsonSerializer.Serialize(metaWithHash, Json));
        MergeSafeWriter.WriteAtomic(ResolveVerifyPath(site, name), JsonSerializer.Serialize(fixture, Json));
        return path;
    }

    /// <summary>
    /// SPEC §Phase 5 drift baseline — update only the meta sidecar
    /// (last_success_hash / last_success_at / adapter_version) without
    /// rewriting source or verify fixture.
    /// </summary>
    public static void SaveMetaOnly(string site, string name, GeneratorMeta meta)
    {
        var metaPath = ResolveMetaPath(site, name);
        if (!File.Exists(ResolvePath(site, name))) return;
        MergeSafeWriter.WriteAtomic(metaPath, JsonSerializer.Serialize(meta, Json));
    }

    public static void Delete(string site, string name)
    {
        foreach (var p in new[] { ResolvePath(site, name), ResolveMetaPath(site, name), ResolveVerifyPath(site, name) })
            if (File.Exists(p)) File.Delete(p);
    }

    public static string Sha256Of(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
